using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Collector;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace IntegrationTests
{
    [Collection("Https5443")]
    public sealed class CollectorOutageTests
    {
        private const int SmallCapacity = 60;
        private const int MinimumDrops = 20;
        private static readonly int[] EquipmentIds = { 1, 2, 3, 4, 5 };
        private readonly ITestOutputHelper output;

        public CollectorOutageTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        [Trait("Category", "Slow")]
        public Task ServerStopped_ShortOutage_NoLossAndOriginalSequences()
        {
            return ExerciseServerStopAsync(exceedCapacity: false);
        }

        [Fact]
        [Trait("Category", "Slow")]
        public Task ServerStopped_LongOutage_DropsOldestAndRecordsCount()
        {
            return ExerciseServerStopAsync(exceedCapacity: true);
        }

        [Fact]
        [Trait("Category", "Slow")]
        [Trait("Category", "Toxiproxy")]
        public Task DatabaseOutage_ShortOutage_NoLoss()
        {
            return ExerciseDatabaseOutageAsync(exceedCapacity: false);
        }

        [Fact]
        [Trait("Category", "Slow")]
        [Trait("Category", "Toxiproxy")]
        public Task DatabaseOutage_LongOutage_DropsOldestAndRecordsCount()
        {
            return ExerciseDatabaseOutageAsync(exceedCapacity: true);
        }

        private async Task ExerciseServerStopAsync(bool exceedCapacity)
        {
            using (var harness = new Harness())
            {
                harness.StartServer();
                var originalState = harness.ServerCollectorState;
                bool stopped = false;
                await ExerciseOutageAsync(harness, () =>
                {
                    stopped = true;
                    harness.StopServer();
                    return Task.CompletedTask;
                }, async () =>
                {
                    if (!stopped)
                        return;

                    await harness.RestartServer();
                    stopped = false;
                    Assert.NotSame(originalState, harness.ServerCollectorState);
                }, exceedCapacity);
            }
        }

        private async Task ExerciseDatabaseOutageAsync(bool exceedCapacity)
        {
            using (var proxy = new ToxiproxyClient("minimes-sql"))
            using (var harness = new Harness())
            using (var unavailable = new DatabaseUnavailableObserver())
            {
                await proxy.CheckReady("[::1]:21433", "127.0.0.1:1433");
                harness.StartServer(databaseViaProxy: true, responseHandler: unavailable);
                await ExerciseOutageAsync(harness, () => proxy.SetEnabled(false), async () =>
                {
                    await proxy.SetEnabled(true);
                    // This scenario creates no toxics. Verify the clean state after restoring connectivity.
                    await proxy.CheckReady("[::1]:21433", "127.0.0.1:1433");
                }, exceedCapacity, unavailable);
            }
        }

        private async Task ExerciseOutageAsync(Harness harness, Func<Task> breakIt, Func<Task> restore,
            bool exceedCapacity, DatabaseUnavailableObserver unavailable = null)
        {
            bool databaseOutage = unavailable != null;
            var settings = new Dictionary<string, string>
            {
                ["HttpRequestTimeoutMilliseconds"] = "1500",
                ["BackoffMaximumMilliseconds"] = "2000"
            };
            if (exceedCapacity)
                settings["SampleBufferCapacity"] = SmallCapacity.ToString(CultureInfo.InvariantCulture);

            using (var overall = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
            {
                var run = harness.StartCollector(overrides: settings);
                var state = run.State;
                SampleRow[] rows = new SampleRow[0];
                string database = null;
                var failures = new JObject[0];
                bool restoreRequired = false;
                string phase = "baseline";
                Func<string> diagnostic = () =>
                    $"SampleCount={state.SampleCount}, SamplesDropped={state.SamplesDropped}, DB rows={rows.Length}, " +
                    $"database={database}, TransmitFailed (failureType/status)={DescribeFailures(failures)}";
                try
                {
                    Assert.Equal(exceedCapacity ? SmallCapacity : 3000, run.Settings.SampleBufferCapacity);
                    await WaitUntilAsync(async token =>
                    {
                        rows = await harness.ReadSamplesAsync(run.Id, token);
                        var server = harness.ServerCollectorState.Read();
                        if (!EquipmentIds.All(id => rows.Any(row => row.EquipmentId == id)) ||
                            !HasEvent(run.ReadLogEntries(), "SampleStored") || !server.LastHeartbeatUtc.HasValue)
                            return false;

                        var baselineAlarms = await harness.ReadTemperatureEventsAsync(run.Id, token);
                        return baselineAlarms.Any(item => item.EventType == (byte)AlarmEventType.ReportNormal);
                    }, TimeSpan.FromSeconds(30), phase, diagnostic, overall.Token);
                    if (databaseOutage)
                    {
                        await WaitUntilAsync(async token =>
                        {
                            database = (string)(await harness.ReadStatus(token))["database"];
                            return database == "connected";
                        }, TimeSpan.FromSeconds(15), "DB connected", diagnostic, overall.Token);
                    }

                    Assert.Equal(0L, state.SamplesDropped);
                    phase = "outage";
                    int unavailableBefore = unavailable?.SampleUnavailableCount ?? 0;
                    var outageStartedUtc = DateTime.UtcNow;
                    restoreRequired = true;
                    await breakIt();
                    var outage = Stopwatch.StartNew();
                    var heartbeatAtBreak = harness.ServerCollectorState.Read().LastHeartbeatUtc.Value;
                    var firstSequences = EquipmentIds.ToDictionary(id => id, state.LastSampleSequence);
                    int initialCount = state.SampleCount;
                    harness.SetTemperature(100f);

                    var outageLogs = new JObject[0];
                    var retained = new SampleItem[0];
                    AlarmEventItem queuedAlarm = null;
                    var minimum = TimeSpan.FromSeconds(databaseOutage ? 8 : 6);
                    var outageLimit = TimeSpan.FromSeconds(databaseOutage ? (exceedCapacity ? 40 : 25) :
                        (exceedCapacity ? 30 : 12));
                    await WaitUntilAsync(async token =>
                    {
                        if (databaseOutage && database != "disconnected")
                            database = (string)(await harness.ReadStatus(token))["database"];
                        outageLogs = run.ReadLogEntries().Where(entry => LoggedSince(entry, outageStartedUtc)).ToArray();
                        failures = outageLogs.Where(entry => IsEvent(entry, "TransmitFailed")).ToArray();
                        retained = state.GetSampleSnapshot();
                        queuedAlarm = state.GetAlarmSnapshot().SingleOrDefault(item => item.EquipmentId == 1 &&
                            item.Cause == AlarmCause.Temperature && item.EventType == AlarmEventType.Raised);
                        bool samplesAdvanced = EquipmentIds.All(id =>
                            retained.Any(item => item.EquipmentId == id && item.SequenceNumber > firstSequences[id]));
                        bool lossObserved = exceedCapacity
                            ? retained.Length == SmallCapacity && state.SamplesDropped >= MinimumDrops &&
                                HasEvent(outageLogs, "SampleBufferDropped")
                            : outage.Elapsed >= minimum && retained.Length > initialCount;
                        bool faultObserved = databaseOutage
                            ? database == "disconnected" && unavailable.SampleUnavailableCount > unavailableBefore &&
                                failures.Any(entry => IsSampleFailure(entry) && IsDatabaseTransmitFailure(entry)) &&
                                harness.ServerCollectorState.Read().LastHeartbeatUtc >= heartbeatAtBreak.AddSeconds(2)
                            : failures.Any(entry => IsSampleFailure(entry) &&
                                IsTransportFailure(entry)) &&
                                HasEvent(outageLogs, "HeartbeatFailed");
                        return samplesAdvanced && queuedAlarm != null && lossObserved && faultObserved;
                    }, outageLimit, phase, diagnostic, overall.Token);

                    if (databaseOutage)
                    {
                        output.WriteLine("DB outage TransmitFailed (failureType/status): {0}; server produced 503 database_unavailable.",
                            DescribeFailures(failures));
                        Assert.All(failures, entry => Assert.True(IsDatabaseTransmitFailure(entry),
                            "DB outage should yield Timeout or ServerError with HTTP 503."));
                        Assert.False(HasEvent(outageLogs, "HeartbeatFailed"));
                    }
                    else
                    {
                        Assert.Single(outageLogs.Where(entry => IsEvent(entry, "HeartbeatFailed")));
                    }

                    long droppedInOutage = state.SamplesDropped;
                    var dropCounts = outageLogs.Where(entry => IsEvent(entry, "SampleBufferDropped"))
                        .Select(entry => (long)entry["Count"]).ToArray();
                    if (exceedCapacity)
                    {
                        Assert.InRange(dropCounts.Last(), 1L, droppedInOutage);
                        Assert.Equal(dropCounts.Distinct().OrderBy(count => count), dropCounts);
                    }
                    else
                    {
                        Assert.Empty(dropCounts);
                        Assert.Equal(0L, droppedInOutage);
                    }

                    rows = await harness.ReadSamplesAsync(run.Id, overall.Token);
                    var storedBeforeRecovery = rows;
                    var storedKeys = new HashSet<(int, long)>(rows.Select(row => (row.EquipmentId, row.SequenceNumber)));
                    Assert.All(retained.Where(item => item.SequenceNumber > firstSequences[item.EquipmentId]),
                        item => Assert.False(storedKeys.Contains((item.EquipmentId, item.SequenceNumber)),
                            $"Outage sample {item.EquipmentId}/{item.SequenceNumber} was stored before recovery."));
                    var outageAlarms = await harness.ReadTemperatureEventsAsync(run.Id, overall.Token);
                    Assert.DoesNotContain(outageAlarms, item => item.EventSequence == queuedAlarm.EventSequence);

                    output.WriteLine("Before restoration after {0:F1}s: {1}", outage.Elapsed.TotalSeconds, diagnostic());
                    phase = "recovery";
                    var outageEndSequences = EquipmentIds.ToDictionary(id => id, state.LastSampleSequence);
                    var recoveryStartedUtc = DateTime.UtcNow;
                    var recovery = Stopwatch.StartNew();
                    await restore();
                    restoreRequired = false;
                    await WaitUntilAsync(async token =>
                    {
                        rows = await harness.ReadSamplesAsync(run.Id, token);
                        if (databaseOutage && database != "connected")
                            database = (string)(await harness.ReadStatus(token))["database"];
                        var recoveredLogs = run.ReadLogEntries()
                            .Where(entry => LoggedSince(entry, recoveryStartedUtc)).ToArray();
                        bool heartbeatRecovered = databaseOutage || HasEvent(recoveredLogs, "HeartbeatRecovered");
                        return (!databaseOutage || database == "connected") && heartbeatRecovered &&
                            HasEvent(recoveredLogs, "TransmitRecovered") &&
                            HasEvent(recoveredLogs, "SampleStored") &&
                            EquipmentIds.All(id => rows.Any(row => row.EquipmentId == id &&
                                row.SequenceNumber >= outageEndSequences[id]));
                    }, TimeSpan.FromSeconds(15) - recovery.Elapsed, phase, diagnostic, overall.Token);

                    output.WriteLine("Recovered after {0:F1}s: {1}", recovery.Elapsed.TotalSeconds, diagnostic());
                    phase = "shutdown";
                    await run.AssertStoppedAsync();
                    overall.Token.ThrowIfCancellationRequested();
                    Assert.Equal(0, state.SampleCount);
                    Assert.Equal(0, state.AlarmCount);
                    Assert.Equal(0, state.UnsentAlarmCount);
                    var finalLogs = run.ReadLogEntries();
                    Assert.False(HasEvent(finalLogs, "BatchDiscarded"));
                    if (databaseOutage)
                        Assert.False(HasEvent(finalLogs.Where(entry => LoggedSince(entry, outageStartedUtc)), "HeartbeatFailed"));
                    else
                        Assert.Single(finalLogs.Where(entry => LoggedSince(entry, outageStartedUtc) &&
                            IsEvent(entry, "HeartbeatRecovered")));

                    rows = await harness.ReadSamplesAsync(run.Id, overall.Token);
                    var alarms = await harness.ReadTemperatureEventsAsync(run.Id, overall.Token);
                    var storedAlarm = Assert.Single(alarms.Where(item => item.EventType == (byte)AlarmEventType.Raised));
                    Assert.Equal(queuedAlarm.EventSequence, storedAlarm.EventSequence);
                    AssertSamples(run, rows, storedBeforeRecovery, retained, outageEndSequences, exceedCapacity);
                    output.WriteLine("After shutdown: {0}", diagnostic());
                }
                catch (OperationCanceledException) when (overall.IsCancellationRequested)
                {
                    Assert.True(false, $"Overall limit exceeded in {phase}. {diagnostic()}");
                }
                finally
                {
                    try
                    {
                        if (restoreRequired)
                            await restore();
                    }
                    finally
                    {
                        await harness.StopCollectorsAsync();
                    }
                }
            }
        }

        private static void AssertSamples(CollectorRun run, SampleRow[] rows, SampleRow[] storedBeforeRecovery,
            SampleItem[] retained, IReadOnlyDictionary<int, long> outageEndSequences, bool exceedCapacity)
        {
            var state = run.State;
            long dropped = state.SamplesDropped;
            long collected = EquipmentIds.Sum(state.LastSampleSequence);
            // A request that timed out can still have committed in Server while its items were
            // pushed out of the full buffer, so those items count as dropped and stored. Every
            // sequence that is not stored must therefore be accounted for by the dropped count.
            long missing = collected - rows.LongLength;
            Assert.True(rows.LongLength <= collected && (exceedCapacity ? missing <= dropped : missing == 0),
                $"Collected={collected}, stored={rows.LongLength}, missing={missing}, dropped={dropped}.");
            Assert.Equal(EquipmentIds, rows.Select(row => row.EquipmentId).Distinct().ToArray());

            foreach (var group in rows.GroupBy(row => row.EquipmentId))
            {
                int id = group.Key;
                long prefixEnd = storedBeforeRecovery.Where(row => row.EquipmentId == id)
                    .Max(row => row.SequenceNumber);
                long previous = 0;
                DateTime? previousUtc = null;
                int gaps = 0;
                foreach (var row in group)
                {
                    if (row.SequenceNumber != previous + 1)
                    {
                        // A timed-out request may commit after the pre-recovery snapshot, so the stored prefix can
                        // extend beyond prefixEnd; there must still be exactly one gap.
                        Assert.True(exceedCapacity && ++gaps == 1 && previous >= prefixEnd &&
                            row.SequenceNumber - 1 <= outageEndSequences[id],
                            $"Equipment {id}: unexpected gap after {previous}, next {row.SequenceNumber}, " +
                            $"last stored before recovery {prefixEnd}, outage end {outageEndSequences[id]}.");
                    }

                    Assert.True(!previousUtc.HasValue || previousUtc.Value <= row.CollectedAtUtc,
                        $"Equipment {id}: collection UTC regressed at sequence {row.SequenceNumber}.");
                    previous = row.SequenceNumber;
                    previousUtc = row.CollectedAtUtc;
                }
                Assert.Equal(state.LastSampleSequence(id), previous);
            }

            if (exceedCapacity)
                Assert.True(dropped >= MinimumDrops);
            else
            {
                Assert.Equal(0L, dropped);
                Assert.False(HasEvent(run.ReadLogEntries(), "SampleBufferDropped"));
            }

            var bySequence = rows.ToDictionary(row => (row.EquipmentId, row.SequenceNumber));
            foreach (var item in retained)
            {
                if (!bySequence.TryGetValue((item.EquipmentId, item.SequenceNumber), out var stored))
                {
                    Assert.True(exceedCapacity, $"Missing buffered sample {item.EquipmentId}/{item.SequenceNumber}.");
                    continue;
                }

                var projectedUtc = run.StartedUtc + item.ReceivedAt;
                Assert.InRange(stored.CollectedAtUtc - projectedUtc,
                    TimeSpan.FromMilliseconds(-500), TimeSpan.FromMilliseconds(500));
            }
        }

        private static bool LoggedSince(JObject entry, DateTime sinceUtc)
        {
            return (DateTimeOffset)entry["@t"] >= sinceUtc;
        }

        private static bool IsEvent(JObject entry, string kind) => (string)entry["EventKind"] == kind;

        private static bool HasEvent(IEnumerable<JObject> entries, string kind) =>
            entries.Any(entry => IsEvent(entry, kind));

        // With the in-process host stopped, Windows may refuse the connection or leave it hanging
        // until the request timeout; Collector handles both as a failed send (keep and retry).
        private static bool IsTransportFailure(JObject entry)
        {
            string type = (string)entry["FailureType"];
            return type == "ConnectionFailed" || type == "Timeout";
        }

        private static bool IsSampleFailure(JObject entry) => (string)entry["Detail"] == "Sample";

        private static string DescribeFailures(IEnumerable<JObject> entries)
        {
            return string.Join(", ", entries.Select(entry =>
                (string)entry["FailureType"] + "/" + ((string)entry["Count"] ?? "-")).Distinct());
        }

        private static bool IsDatabaseTransmitFailure(JObject entry)
        {
            var failure = (string)entry["FailureType"];
            return failure == "Timeout" || (failure == "ServerError" && (int?)entry["Count"] == 503);
        }

        private static async Task WaitUntilAsync(Func<CancellationToken, Task<bool>> ready, TimeSpan limit,
            string phase, Func<string> diagnostic, CancellationToken overallToken)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(overallToken))
            {
                if (limit > TimeSpan.Zero)
                {
                    timeout.CancelAfter(limit);
                    try
                    {
                        while (!timeout.IsCancellationRequested)
                        {
                            if (await ready(timeout.Token) && !timeout.IsCancellationRequested)
                                return;
                            await Task.Delay(200, timeout.Token);
                        }
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                    {
                    }
                }
                Assert.True(false, $"Timed out in {phase}. {diagnostic()}");
            }
        }

        private sealed class DatabaseUnavailableObserver : DelegatingHandler
        {
            private int sampleUnavailable;

            public int SampleUnavailableCount => Volatile.Read(ref sampleUnavailable);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var response = await base.SendAsync(request, cancellationToken);
                if (request.RequestUri.AbsolutePath == "/api/v1/collector/samples" &&
                    response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    if (response.Content is ObjectContent content &&
                        (string)JObject.FromObject(content.Value)["error"] == "database_unavailable")
                        Interlocked.Increment(ref sampleUnavailable);
                }
                return response;
            }
        }
    }
}
