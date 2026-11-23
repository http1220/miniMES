using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Collector;
using Newtonsoft.Json.Linq;
using Xunit;

namespace IntegrationTests
{
    [Collection("Https5443")]
    public sealed class CollectorToxiproxyTests
    {
        private const string ProxyUrl = "https://localhost:5444";
        private static readonly int[] HealthyEquipment = { 2, 3, 4, 5 };
        private static readonly TimeSpan StartupLimit = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan RecoveryLimit = TimeSpan.FromSeconds(15);
        private static readonly IReadOnlyDictionary<string, string> FaultSettings = new Dictionary<string, string>
        {
            ["HttpRequestTimeoutMilliseconds"] = "1500"
        };

        [Fact]
        [Trait("Category", "Toxiproxy")]
        [Trait("Category", "Slow")]
        public Task RunSupervised_ResponseLatency_PollingAndJudgeContinue()
        {
            const string toxicName = "collector-response-latency";
            return WithProxyAsync(toxicName, async (harness, proxy) =>
            {
                harness.StartServer();
                var run = harness.StartCollector(ProxyUrl, FaultSettings);
                await WaitForBaselineAsync(harness, run);

                await proxy.AddToxic(toxicName, "latency", "downstream",
                    new JObject { ["latency"] = 4000, ["jitter"] = 0 });
                var faultStartUtc = DateTime.UtcNow;
                var elapsed = Stopwatch.StartNew();
                harness.SetDeviceDelay(1, 3000);
                var initialSequences = HealthyEquipment.Select(run.State.LastSampleSequence).ToArray();
                var previousSequences = initialSequences;
                TimeSpan? alarmDetectedAt = null;
                var checkpoint = TimeSpan.FromSeconds(4);

                // A TCP-level latency toxic also delays TLS handshakes, so heartbeat requests cannot
                // reach Server here; that part is proven by the slow-endpoint test below.
                while (elapsed.Elapsed < TimeSpan.FromSeconds(12))
                {
                    if (!alarmDetectedAt.HasValue && run.State.GetAlarmSnapshot().Any(item =>
                        item.EquipmentId == 1 && item.Cause == AlarmCause.Communication &&
                        (item.EventType == AlarmEventType.Raised || item.EventType == AlarmEventType.ReportAbnormal)))
                        alarmDetectedAt = elapsed.Elapsed;

                    if (elapsed.Elapsed >= checkpoint)
                    {
                        var current = HealthyEquipment.Select(run.State.LastSampleSequence).ToArray();
                        AssertPollingAdvanced(previousSequences, current, 1);
                        previousSequences = current;
                        checkpoint += TimeSpan.FromSeconds(4);
                    }
                    await Task.Delay(100);
                }

                var finalSequences = HealthyEquipment.Select(run.State.LastSampleSequence).ToArray();
                AssertPollingAdvanced(initialSequences, finalSequences, 10);
                var alarmLimit = run.Settings.CommunicationFault + TimeSpan.FromSeconds(6);
                Assert.True(alarmDetectedAt.HasValue && alarmDetectedAt.Value <= alarmLimit,
                    $"Equipment 1 communication alarm observed at {alarmDetectedAt}; expected within {alarmLimit}.");
                int timeouts = Failures(run, "Timeout", faultStartUtc).Length;
                Assert.True(timeouts >= 2, $"Expected at least two TransmitFailed/Timeout entries, observed {timeouts}.");
                var buffered = run.State.GetSampleSnapshot();
                Assert.NotEmpty(buffered);
                await proxy.RemoveToxic(toxicName, ignoreNotFound: false);
                harness.SetDeviceDelay(1, 100);
                await WaitAsync(token => harness.ReadSamplesAsync(run.Id, token),
                    rows => HealthyEquipment.Select((id, index) => rows.Any(row => row.EquipmentId == id &&
                        row.SequenceNumber > finalSequences[index])).All(stored => stored) &&
                        run.ReadLogEntries().Any(entry => (string)entry["EventKind"] == "TransmitRecovered" &&
                            LogTime(entry) >= faultStartUtc), RecoveryLimit,
                    rows => $"Transmission did not recover after removing latency. Stored samples: {rows?.Length}.");

                await run.AssertStoppedAsync();
                Assert.Equal(0L, run.State.SamplesDropped);
                var samples = await harness.ReadSamplesAsync(run.Id);
                var storedKeys = new HashSet<(int, long)>(samples.Select(row => (row.EquipmentId, row.SequenceNumber)));
                Assert.All(buffered, item => Assert.Contains((item.EquipmentId, item.SequenceNumber), storedKeys));
                foreach (var group in samples.GroupBy(row => row.EquipmentId))
                    Assert.Equal(Enumerable.Range(1, group.Count()).Select(sequence => (long)sequence),
                        group.Select(row => row.SequenceNumber));
            });
        }

        [Fact]
        [Trait("Category", "Slow")]
        public async Task RunSupervised_SlowTransmitEndpoints_HeartbeatStillReachesServer()
        {
            // D-134: a transmit request stuck in its timeout must not queue heartbeat behind it.
            // Only samples and alarm-events are delayed (in Server, selectively), so the
            // heartbeat endpoint stays fast and each heartbeat must reach Server on time.
            var slow = new DelayTransmitEndpoints();
            using (var harness = new Harness())
            {
                harness.StartServer(slow);
                var run = harness.StartCollector("https://localhost:5443", FaultSettings);
                try
                {
                    await WaitForBaselineAsync(harness, run);
                    slow.DelayMilliseconds = 4000;
                    var faultStartUtc = DateTime.UtcNow;
                    var elapsed = Stopwatch.StartNew();
                    var initialSequences = HealthyEquipment.Select(run.State.LastSampleSequence).ToArray();
                    var lastHeartbeat = harness.ServerCollectorState.Read().LastHeartbeatUtc ?? faultStartUtc;
                    int heartbeatAdvances = 0;
                    while (elapsed.Elapsed < TimeSpan.FromSeconds(12))
                    {
                        var heartbeat = harness.ServerCollectorState.Read().LastHeartbeatUtc;
                        if (heartbeat.HasValue && heartbeat.Value - lastHeartbeat >= TimeSpan.FromSeconds(2))
                        {
                            lastHeartbeat = heartbeat.Value;
                            heartbeatAdvances++;
                        }
                        await Task.Delay(100);
                    }

                    int timeouts = Failures(run, "Timeout", faultStartUtc).Length;
                    Assert.True(timeouts >= 2, $"Expected at least two TransmitFailed/Timeout entries, observed {timeouts}.");
                    Assert.True(heartbeatAdvances >= 3,
                        $"Expected at least three Server heartbeat advances spaced by 2 seconds while transmit timed out, observed {heartbeatAdvances}.");
                    AssertPollingAdvanced(initialSequences, HealthyEquipment.Select(run.State.LastSampleSequence).ToArray(), 10);
                    Assert.DoesNotContain(run.ReadLogEntries(), entry => (string)entry["EventKind"] == "HeartbeatFailed" &&
                        LogTime(entry) >= faultStartUtc);

                    slow.DelayMilliseconds = 0;
                    await run.AssertStoppedAsync();
                }
                finally
                {
                    slow.DelayMilliseconds = 0;
                    await harness.StopCollectorsAsync();
                }
            }
        }

        [Fact]
        [Trait("Category", "Slow")]
        public async Task RunSupervised_CertificateNameMismatch_ClassifiedAsCertificateFailed()
        {
            using (var harness = new Harness())
            {
                harness.StartServer();
                using (var trusted = new HttpClient(new HttpClientHandler { UseProxy = false })
                    { Timeout = TimeSpan.FromSeconds(5) })
                using (var response = await trusted.GetAsync("https://localhost:5443/api/v1/status"))
                    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

                // A cold run with no successful heartbeat cannot send samples without a time reference.
                var run = harness.StartCollector("https://127.0.0.1:5443", seedTimeReference: true);
                try
                {
                    await WaitAsync(_ => Task.FromResult(Failures(run, "CertificateFailed", run.StartedUtc)),
                        entries => entries.Any(entry => (string)entry["Detail"] == "Sample"), StartupLimit,
                        _ => "Expected TransmitFailed/CertificateFailed for the IP literal absent from the certificate SAN.");
                    int baseline = run.State.SampleCount;
                    await WaitAsync(_ => Task.FromResult(run.State.SampleCount), count => count >= baseline + 5,
                        TimeSpan.FromSeconds(5), count => $"Sample buffer did not grow from {baseline}; last count {count}.");
                    await run.AssertStoppedAsync(ExitCodes.ShutdownIncomplete);
                    Assert.Empty(await harness.ReadSamplesAsync(run.Id));
                    Assert.Null(harness.ServerCollectorState.Read().LastHeartbeatUtc);
                }
                finally
                {
                    await harness.StopCollectorsAsync();
                }
            }
        }

        [Fact]
        [Trait("Category", "Toxiproxy")]
        [Trait("Category", "Slow")]
        public Task RunSupervised_StalledResponse_ClassifiedAsTimeout()
        {
            const string toxicName = "collector-stalled-response";
            return WithProxyAsync(toxicName, async (harness, proxy) =>
            {
                harness.StartServer(new PadSampleResponse());
                var run = harness.StartCollector(ProxyUrl, FaultSettings, observeSampleHeaders: true);
                await WaitForBaselineAsync(harness, run);

                // The slicer operates on encrypted TCP bytes. Keep the slices fast enough
                // for TLS and HTTP headers, and use a larger valid JSON body to stall buffering.
                await proxy.AddToxic(toxicName, "slicer", "downstream",
                    new JObject { ["average_size"] = 512, ["size_variation"] = 0, ["delay"] = 20000 });
                var faultStartUtc = DateTime.UtcNow;
                var match = await WaitAsync(_ => Task.FromResult(FindBodyTimeout(run, faultStartUtc)),
                    attempt => attempt.HasValue, RecoveryLimit,
                    _ => DescribeStall(run, faultStartUtc));
                Assert.InRange(match.Value, TimeSpan.Zero, run.Settings.HttpRequestTimeout + TimeSpan.FromSeconds(1));

                await proxy.RemoveToxic(toxicName, ignoreNotFound: false);
                await run.AssertStoppedAsync();
            });
        }

        private static string DescribeStall(CollectorRun run, DateTime sinceUtc)
        {
            int requests = run.SampleRequests.Count(request => request.StartedUtc >= sinceUtc);
            int headers = run.SampleRequests.Count(request => request.StartedUtc >= sinceUtc &&
                request.SuccessHeadersUtc != DateTime.MinValue);
            int timeouts = Failures(run, "Timeout", sinceUtc).Count(entry => (string)entry["Detail"] == "Sample");
            return "Slicer did not reproduce a sample body timeout. " +
                "Post-install sample requests: " + requests + "; success headers: " + headers +
                "; sample timeout logs: " + timeouts + ".";
        }

        private static TimeSpan? FindBodyTimeout(CollectorRun run, DateTime sinceUtc)
        {
            var failures = Failures(run, "Timeout", sinceUtc);
            var requests = run.SampleRequests;
            // Sample sends are serial. Match a failure to the last sample request started before it.
            foreach (var failure in failures.Where(entry => (string)entry["Detail"] == "Sample"))
            {
                var finishedUtc = LogTime(failure);
                var request = requests.LastOrDefault(item => item.StartedUtc <= finishedUtc);
                if (request != null && request.StartedUtc >= sinceUtc &&
                    request.SuccessHeadersUtc >= request.StartedUtc && request.SuccessHeadersUtc <= finishedUtc)
                    return finishedUtc - request.StartedUtc;
            }
            return null;
        }

        private static JObject[] Failures(CollectorRun run, string type, DateTime sinceUtc)
        {
            return run.ReadLogEntries().Where(entry => (string)entry["EventKind"] == "TransmitFailed" &&
                (string)entry["FailureType"] == type && LogTime(entry) >= sinceUtc).ToArray();
        }

        private static DateTime LogTime(JObject entry) => ((DateTimeOffset)entry["@t"]).UtcDateTime;

        private static void AssertPollingAdvanced(long[] before, long[] after, int minimum)
        {
            for (int index = 0; index < HealthyEquipment.Length; index++)
                Assert.True(after[index] - before[index] >= minimum,
                    $"Equipment {HealthyEquipment[index]} sequence {before[index]} -> {after[index]}; expected +{minimum}.");
        }

        private static Task<SampleRow[]> WaitForBaselineAsync(Harness harness, CollectorRun run)
        {
            return WaitAsync(token => harness.ReadSamplesAsync(run.Id, token),
                rows => Enumerable.Range(1, 5).All(id => rows.Any(row => row.EquipmentId == id)) &&
                    run.ReadLogEntries().Any(entry => (string)entry["EventKind"] == "SampleStored"), StartupLimit,
                rows => $"No successful baseline send with samples for all five devices. Stored samples: {rows?.Length}.");
        }

        private static async Task<T> WaitAsync<T>(Func<CancellationToken, Task<T>> read, Func<T, bool> ready,
            TimeSpan timeout, Func<T, string> failure)
        {
            using (var deadline = new CancellationTokenSource(timeout))
            {
                T latest = default(T);
                try
                {
                    while (!deadline.IsCancellationRequested)
                    {
                        latest = await read(deadline.Token);
                        if (!deadline.IsCancellationRequested && ready(latest))
                            return latest;
                        await Task.Delay(100, deadline.Token);
                    }
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                }
                catch (Microsoft.Data.SqlClient.SqlException) when (deadline.IsCancellationRequested)
                {
                }
                Assert.True(false, failure(latest));
                return latest;
            }
        }

        private static async Task WithProxyAsync(string toxicName, Func<Harness, ToxiproxyClient, Task> scenario)
        {
            using (var harness = new Harness())
            using (var proxy = new ToxiproxyClient("minimes-server"))
            {
                await proxy.CheckReady("[::1]:5444", "[::1]:5443");
                try
                {
                    await scenario(harness, proxy);
                }
                finally
                {
                    try
                    {
                        await proxy.RemoveToxic(toxicName, ignoreNotFound: true);
                    }
                    finally
                    {
                        try { await proxy.SetEnabled(true); }
                        finally { await harness.StopCollectorsAsync(); }
                    }
                }
            }
        }

        private sealed class DelayTransmitEndpoints : DelegatingHandler
        {
            private int delayMilliseconds;

            public int DelayMilliseconds
            {
                set { Volatile.Write(ref delayMilliseconds, value); }
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                int delay = Volatile.Read(ref delayMilliseconds);
                string path = request.RequestUri.AbsolutePath;
                if (delay > 0 && (path == "/api/v1/collector/samples" || path == "/api/v1/collector/alarm-events"))
                    await Task.Delay(delay, cancellationToken);
                return await base.SendAsync(request, cancellationToken);
            }
        }

        private sealed class PadSampleResponse : DelegatingHandler
        {
            private static readonly string JsonWhitespace = new string(' ', 256 * 1024);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var response = await base.SendAsync(request, cancellationToken);
                if (request.RequestUri.AbsolutePath == "/api/v1/collector/samples" && response.IsSuccessStatusCode)
                {
                    using (var original = response.Content)
                    {
                        var json = await original.ReadAsStringAsync();
                        response.Content = new StringContent(json + JsonWhitespace, Encoding.UTF8, "application/json");
                    }
                }
                return response;
            }
        }
    }
}
