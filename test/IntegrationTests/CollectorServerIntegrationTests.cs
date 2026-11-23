using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Collector;
using Core;
using Dapper;
using Data;
using Microsoft.Data.SqlClient;
using Microsoft.Owin.Hosting;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Formatting.Compact;
using Server;
using Xunit;
using CollectorState = Collector.CollectorState;

namespace IntegrationTests
{
    [CollectionDefinition("Https5443", DisableParallelization = true)]
    public sealed class Https5443Collection
    {
    }

    [Collection("Https5443")]
    public sealed class CollectorServerIntegrationTests
    {
        private const string CollectorKey = "fake-collector-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string DashboardKey = "fake-dashboard-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const byte TemperatureCause = 2;

        [Fact]
        [Trait("Category", "Slow")]
        public Task NormalRun_StoresSamplesForAllEquipmentAndShutsDown()
        {
            return WithHarnessAsync(async harness =>
            {
                harness.StartServer();
                var run = harness.StartCollector();
                await WaitForSamplesAsync(harness, run, rows => Enumerable.Range(1, 5)
                    .All(id => rows.Count(row => row.EquipmentId == id) >= 2));

                await run.StopAsync();
                Assert.Equal(1, harness.RunCount(run.Id));
                AssertSampleSequences(await harness.ReadSamplesAsync(run.Id), 2);
            });
        }

        [Fact]
        [Trait("Category", "Slow")]
        public Task Fs15_StoresBufferedSamplesWithOriginalSequenceAndCollectionTime()
        {
            return WithHarnessAsync(async harness =>
            {
                var run = harness.StartCollector();
                var buffered = await WaitForOfflineSamplesAsync(run);
                var serverStartUtc = DateTime.UtcNow;
                harness.StartServer();

                var stored = await WaitForSamplesAsync(harness, run, rows => Enumerable.Range(1, 5)
                    .All(id => rows.Count(row => row.EquipmentId == id) >=
                        buffered.Count(item => item.EquipmentId == id)));
                AssertSampleSequences(stored, 3);
                Assert.Equal(1, harness.RunCount(run.Id));

                var storedByKey = stored.ToDictionary(row => (row.EquipmentId, row.SequenceNumber));
                var firstSuccessUtc = run.FirstSuccessUtc;
                foreach (var group in buffered.GroupBy(item => item.EquipmentId))
                {
                    DateTime? previousUtc = null;
                    foreach (var item in group)
                    {
                        var row = storedByKey[(item.EquipmentId, item.SequenceNumber)];
                        Assert.Equal(item.Temperature, row.Temperature);
                        Assert.True(row.CollectedAtUtc >= run.StartedUtc &&
                            row.CollectedAtUtc < serverStartUtc && row.CollectedAtUtc < firstSuccessUtc,
                            $"Equipment {row.EquipmentId} sequence {row.SequenceNumber}: collection UTC {row.CollectedAtUtc:O}, " +
                            $"Collector start {run.StartedUtc:O}, Server startup {serverStartUtc:O}, first success {firstSuccessUtc:O}.");
                        Assert.InRange(row.CollectedAtUtc - (run.StartedUtc + item.ReceivedAt),
                            TimeSpan.FromMilliseconds(-500), TimeSpan.FromMilliseconds(500));
                        Assert.True(!previousUtc.HasValue || row.CollectedAtUtc > previousUtc.Value,
                            $"Equipment {row.EquipmentId} sequence {row.SequenceNumber}: buffered sample UTC did not advance.");
                        previousUtc = row.CollectedAtUtc;
                    }
                }
            });
        }

        [Fact]
        [Trait("Category", "Slow")]
        public Task Fs12_RestartWithAbnormalTemperature_ReportsOnceAndKeepsActiveAlarm()
        {
            return WithHarnessAsync(harness => CheckRestartAsync(harness, returnToNormal: false));
        }

        [Fact]
        [Trait("Category", "Slow")]
        public Task Fs12_RestartWithNormalTemperature_ReportsAfterConsecutiveSamplesAndClearsAlarm()
        {
            return WithHarnessAsync(harness => CheckRestartAsync(harness, returnToNormal: true));
        }

        [Fact]
        [Trait("Category", "Slow")]
        public Task Fs12_StartWithoutServer_StoresBufferedAbnormalReportWithOriginalTime()
        {
            return WithHarnessAsync(async harness =>
            {
                harness.SetTemperature(100f);
                var run = harness.StartCollector();
                await WaitForOfflineSamplesAsync(run);
                var bufferedReport = await PollAsync(_ => Task.FromResult(run.State.GetAlarmSnapshot()),
                    events => events.Any(IsDeviceOneTemperature),
                    events => $"SAMPLE=0, ALARM_EVENT=0 (server not started), buffered alarms={events.Length}");
                var expected = Assert.Single(bufferedReport.Where(IsDeviceOneTemperature));
                Assert.Equal(AlarmEventType.ReportAbnormal, expected.EventType);
                Assert.False(run.Timeline.HasReference);

                var serverStartUtc = DateTime.UtcNow;
                harness.StartServer();
                await WaitForTemperatureEventsAsync(harness, run);
                await run.StopAsync();
                var report = Assert.Single(await harness.ReadTemperatureEventsAsync(run.Id));
                Assert.Equal((byte)3, report.EventType);
                Assert.Equal(expected.EventSequence, report.EventSequence);
                Assert.Equal(1, harness.RunCount(run.Id));
                Assert.True(report.OccurredAtUtc >= run.StartedUtc && report.OccurredAtUtc < serverStartUtc);
                Assert.True(report.OccurredAtUtc < run.FirstSuccessUtc);
                Assert.InRange(report.OccurredAtUtc - (run.StartedUtc + expected.OccurredAt),
                    TimeSpan.FromMilliseconds(-500), TimeSpan.FromMilliseconds(500));
                var alarm = Assert.Single(harness.ReadTemperatureAlarms());
                Assert.Null(alarm.ClearedAtUtc);
                Assert.Equal((int?)alarm.AlarmId, report.AlarmId);
                Assert.Equal(report.OccurredAtUtc, alarm.RaisedAtUtc);
            });
        }

        private static async Task CheckRestartAsync(Harness harness, bool returnToNormal)
        {
            harness.SetTemperature(100f);
            harness.StartServer();
            var initial = harness.StartCollector();
            await WaitForTemperatureEventsAsync(harness, initial);
            await initial.StopAsync();

            var originalAlarm = Assert.Single(harness.ReadTemperatureAlarms());
            Assert.Null(originalAlarm.ClearedAtUtc);
            Assert.Equal((byte)3, Assert.Single(await harness.ReadTemperatureEventsAsync(initial.Id)).EventType);

            if (returnToNormal)
                harness.SetTemperature(23.456f);

            var restarted = harness.StartCollector();
            Assert.NotEqual(initial.Id, restarted.Id);
            await WaitForTemperatureEventsAsync(harness, restarted);
            await WaitForSamplesAsync(harness, restarted, rows => Enumerable.Range(1, 5)
                .All(id => rows.Count(row => row.EquipmentId == id) >= restarted.Settings.ThresholdConsecutiveCount));
            await restarted.StopAsync();

            Assert.Equal(1, harness.RunCount(initial.Id));
            Assert.Equal(1, harness.RunCount(restarted.Id));
            var sequences = harness.ReadEventSequences(restarted.Id);
            Assert.Equal(Enumerable.Range(1, sequences.Length).Select(value => (long)value), sequences);

            var samples = await harness.ReadSamplesAsync(restarted.Id);
            AssertSampleSequences(samples, restarted.Settings.ThresholdConsecutiveCount);
            var report = Assert.Single(await harness.ReadTemperatureEventsAsync(restarted.Id));
            Assert.Equal(returnToNormal ? (byte)4 : (byte)3, report.EventType);
            var alarm = Assert.Single(harness.ReadTemperatureAlarms());
            Assert.Equal(originalAlarm.AlarmId, alarm.AlarmId);

            if (returnToNormal)
            {
                Assert.Equal((int?)originalAlarm.AlarmId, report.AlarmId);
                Assert.Equal((DateTime?)report.OccurredAtUtc, alarm.ClearedAtUtc);
                var firstNormalSamples = samples.Where(row => row.EquipmentId == 1)
                    .Take(restarted.Settings.ThresholdConsecutiveCount).ToArray();
                Assert.All(firstNormalSamples, sample => Assert.Equal((float?)23.456f, sample.Temperature));
                var lastRequired = firstNormalSamples.Last();
                // The sample and its report share a Modbus receipt time. Separate batches
                // can use different reference points, so allow less than one polling interval.
                var tolerance = TimeSpan.FromTicks(restarted.Settings.PollInterval.Ticks / 2);
                Assert.InRange(report.OccurredAtUtc - lastRequired.CollectedAtUtc, -tolerance, tolerance);
            }
            else
            {
                Assert.Null(alarm.ClearedAtUtc);
                Assert.Equal(originalAlarm.RaisedAtUtc, alarm.RaisedAtUtc);
            }
        }

        private static bool IsDeviceOneTemperature(AlarmEventItem item)
        {
            return item.EquipmentId == 1 && item.Cause == AlarmCause.Temperature;
        }

        private static async Task<SampleItem[]> WaitForOfflineSamplesAsync(CollectorRun run)
        {
            var samples = await PollAsync(_ => Task.FromResult(run.State.GetSampleSnapshot()),
                rows => Enumerable.Range(1, 5).All(id => rows.Count(row => row.EquipmentId == id) >= 3)
                    && rows.Last().ReceivedAt - rows.First().ReceivedAt >= TimeSpan.FromSeconds(1),
                rows => $"SAMPLE=0, ALARM_EVENT=0 (server not started), buffered samples={rows.Length}");
            Assert.True(run.State.SampleCount > 0);
            Assert.False(run.Timeline.HasReference);
            Assert.Equal(0L, run.State.SamplesDropped);
            return samples;
        }

        private static void AssertSampleSequences(SampleRow[] samples, int minimumPerEquipment)
        {
            Assert.Equal(Enumerable.Range(1, 5), samples.Select(row => row.EquipmentId).Distinct());
            foreach (var group in samples.GroupBy(row => row.EquipmentId))
            {
                var rows = group.ToArray();
                Assert.True(rows.Length >= minimumPerEquipment,
                    $"Equipment {group.Key} stored {rows.Length} samples.");
                for (int index = 0; index < rows.Length; index++)
                {
                    Assert.Equal(index + 1L, rows[index].SequenceNumber);
                    if (index > 0)
                        Assert.True(rows[index - 1].CollectedAtUtc <= rows[index].CollectedAtUtc,
                            $"Equipment {group.Key} UTC regressed at sequence {rows[index].SequenceNumber}.");
                }
            }
        }

        private static Task<SampleRow[]> WaitForSamplesAsync(Harness harness, CollectorRun run,
            Func<SampleRow[], bool> ready)
        {
            return PollAsync(token => harness.ReadSamplesAsync(run.Id, token), ready,
                rows => "SAMPLE=" + rows.Length + " (equipment 1–5: " + string.Join(", ",
                    Enumerable.Range(1, 5).Select(id => rows.Count(row => row.EquipmentId == id))) + ")");
        }

        private static Task<AlarmEventRow[]> WaitForTemperatureEventsAsync(Harness harness, CollectorRun run)
        {
            return PollAsync(token => harness.ReadTemperatureEventsAsync(run.Id, token), rows => rows.Length > 0,
                rows => $"ALARM_EVENT (equipment 1, temperature)={rows.Length}");
        }

        private static async Task<T> PollAsync<T>(Func<CancellationToken, Task<T>> read,
            Func<T, bool> ready, Func<T, string> counts)
        {
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                string lastCounts = "no completed read";
                try
                {
                    while (!deadline.IsCancellationRequested)
                    {
                        var latest = await read(deadline.Token);
                        lastCounts = counts(latest);
                        if (!deadline.IsCancellationRequested && ready(latest))
                            return latest;
                        await Task.Delay(100, deadline.Token);
                    }
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                }
                catch (SqlException) when (deadline.IsCancellationRequested)
                {
                }

                Assert.True(false, "Timed out waiting for Collector integration state. Last counts: " + lastCounts);
                return default(T);
            }
        }

        private static async Task WithHarnessAsync(Func<Harness, Task> scenario)
        {
            using (var harness = new Harness())
            {
                try
                {
                    await scenario(harness);
                }
                finally
                {
                    await harness.StopCollectorsAsync();
                }
                await harness.AssertCollectorsStoppedAsync();
            }
        }

        private sealed class Harness : IDisposable
        {
            private readonly string databaseName = "MiniMES_CollectorIt_" + Guid.NewGuid().ToString("N");
            private readonly string directory = Path.Combine(Path.GetTempPath(), "MiniMES_CollectorIt_" + Guid.NewGuid().ToString("N"));
            private readonly FakeModbusDevice[] devices = new FakeModbusDevice[5];
            private readonly List<CollectorRun> runs = new List<CollectorRun>();
            private readonly string configured;
            private SqlConnection admin;
            private bool created;
            private SqlConnection connection;
            private DatabaseMonitor databaseMonitor;
            private Serilog.Core.Logger serverLog;
            private IDisposable host;

            public Harness()
            {
                configured = Environment.GetEnvironmentVariable("MINIMES_TEST_SQL_CONNECTION_STRING");
                Assert.False(string.IsNullOrWhiteSpace(configured), "Set MINIMES_TEST_SQL_CONNECTION_STRING for a test SQL Server with CREATE DATABASE permission.");
                try
                {
                    Directory.CreateDirectory(directory);
                    for (int index = 0; index < devices.Length; index++)
                    {
                        devices[index] = new FakeModbusDevice(index % 2 == 0
                            ? RegisterWordOrder.ABCD : RegisterWordOrder.CDAB);
                        // Allow for the small clock projection error from the local HTTPS response.
                        devices[index].DelayMilliseconds = 100;
                    }
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void SetTemperature(float temperature)
            {
                devices[0].SetValues(temperature, 7.89f, 2, RegisterWordOrder.ABCD);
            }

            public void StartServer()
            {
                var builder = new SqlConnectionStringBuilder(configured)
                {
                    InitialCatalog = "master", Pooling = false, ConnectTimeout = 5
                };
                admin = new SqlConnection(builder.ConnectionString);
                admin.Open();
                admin.Execute("CREATE DATABASE [" + databaseName + "]");
                created = true;

                builder.InitialCatalog = databaseName;
                string testConnection = builder.ConnectionString;
                MigrationRunner.Run(testConnection);
                connection = new SqlConnection(testConnection);
                connection.Open();

                var values = new Dictionary<string, string>
                {
                    ["SERVER_URL"] = "https://+:5443/",
                    ["DB_CONNECTION_STRING"] = testConnection,
                    ["COLLECTOR_API_KEY"] = CollectorKey,
                    ["DASHBOARD_API_KEY"] = DashboardKey
                };
                Assert.Empty(ServerSettings.Validate(values, out var settings));
                serverLog = new LoggerConfiguration()
                    .WriteTo.File(new CompactJsonFormatter(), Path.Combine(directory, "server.log")).CreateLogger();
                var probe = new DatabaseProbe(settings.ConnectionString, settings.ProbeTimeoutSeconds);
                databaseMonitor = new DatabaseMonitor(() => probe.Check(), TimeSpan.FromSeconds(settings.ProbeIntervalSeconds));
                var time = CollectorTime.StartReal();
                var runtime = new ApiRuntime(settings, new Server.CollectorState(time.Now,
                    TimeSpan.FromSeconds(settings.CollectorStoppedSeconds)), databaseMonitor, () => DateTime.UtcNow, serverLog);
                host = WebApp.Start(settings.ServerUrl, app => ApiConfiguration.Configure(app, runtime));
                databaseMonitor.Start();
            }

            public CollectorRun StartCollector()
            {
                var logDirectory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
                var values = new Dictionary<string, string>
                {
                    ["SERVER_BASE_URL"] = "https://localhost:5443",
                    ["COLLECTOR_API_KEY"] = CollectorKey,
                    ["LogDirectory"] = logDirectory,
                    ["PollIntervalMilliseconds"] = "400",
                    ["ReadTimeoutMilliseconds"] = "350",
                    ["HeartbeatIntervalMilliseconds"] = "250",
                    ["ThresholdConsecutiveCount"] = "3"
                };
                for (int index = 0; index < devices.Length; index++)
                    values[$"Equipment.{index + 1}.Port"] = devices[index].Port.ToString(CultureInfo.InvariantCulture);

                Assert.Empty(CollectorSettings.Validate(values, out var settings));
                var run = new CollectorRun(settings);
                runs.Add(run);
                return run;
            }

            public async Task<SampleRow[]> ReadSamplesAsync(Guid runId, CancellationToken token = default(CancellationToken))
            {
                var rows = await connection.QueryAsync<SampleRow>(new CommandDefinition(@"
SELECT EquipmentId, SequenceNumber, CollectedAtUtc, Temperature
FROM dbo.SAMPLE WHERE RunId = @runId ORDER BY EquipmentId, SequenceNumber",
                    new { runId }, commandTimeout: 5, cancellationToken: token));
                return rows.ToArray();
            }

            public async Task<AlarmEventRow[]> ReadTemperatureEventsAsync(Guid runId,
                CancellationToken token = default(CancellationToken))
            {
                var rows = await connection.QueryAsync<AlarmEventRow>(new CommandDefinition(@"
SELECT EventSequence, EventType, OccurredAtUtc, AlarmId FROM dbo.ALARM_EVENT
WHERE RunId = @runId AND EquipmentId = 1 AND Cause = @TemperatureCause ORDER BY EventSequence",
                    new { runId, TemperatureCause }, commandTimeout: 5, cancellationToken: token));
                return rows.ToArray();
            }

            public AlarmRow[] ReadTemperatureAlarms()
            {
                return connection.Query<AlarmRow>(@"
SELECT AlarmId, RaisedAtUtc, ClearedAtUtc FROM dbo.ALARM
WHERE EquipmentId = 1 AND Cause = @TemperatureCause",
                    new { TemperatureCause }, commandTimeout: 2).ToArray();
            }

            public long[] ReadEventSequences(Guid runId)
            {
                return connection.Query<long>(@"
SELECT EventSequence FROM dbo.ALARM_EVENT WHERE RunId = @runId ORDER BY EventSequence",
                    new { runId }, commandTimeout: 2).ToArray();
            }

            public int RunCount(Guid runId)
            {
                return connection.QuerySingle<int>("SELECT COUNT(*) FROM dbo.COLLECTOR_RUN WHERE RunId = @runId",
                    new { runId }, commandTimeout: 2);
            }

            public void Dispose()
            {
                try
                {
                    try
                    {
                        host?.Dispose();
                    }
                    finally
                    {
                        databaseMonitor?.Dispose();
                        serverLog?.Dispose();
                        connection?.Dispose();
                    }

                    foreach (var run in runs)
                        run.AssertResponseKeysAbsent();
                    if (Directory.Exists(directory))
                    {
                        foreach (var path in Directory.GetFiles(directory, "*.log", SearchOption.AllDirectories))
                            AssertNoKeys(File.ReadAllText(path));
                    }
                }
                finally
                {
                    try
                    {
                        if (created)
                        {
                            admin.Execute("ALTER DATABASE [" + databaseName + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
                            admin.Execute("DROP DATABASE [" + databaseName + "]");
                        }
                    }
                    finally
                    {
                        admin?.Dispose();
                        foreach (var device in devices)
                            device?.Dispose();
                        if (Directory.Exists(directory))
                            Directory.Delete(directory, true);
                    }
                }
            }

            public Task StopCollectorsAsync()
            {
                return Task.WhenAll(runs.Select(run => run.StopAsync()));
            }

            public async Task AssertCollectorsStoppedAsync()
            {
                foreach (var run in runs)
                    await run.AssertStoppedAsync();
            }
        }

        private sealed class CollectorRun
        {
            private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
            private readonly CollectorLog log;
            private readonly HttpServerClient client;
            private readonly Task<int> running;
            private Task<int> stopped;

            public CollectorRun(CollectorSettings settings)
            {
                Settings = settings;
                var time = CollectorTime.StartReal();
                State = new CollectorState(Guid.NewGuid(), settings.Equipment.Select(device => device.Id),
                    settings.SampleBufferCapacity, settings.AlarmQueueCapacity,
                    settings.CommunicationRecoverySuccessCount, settings.ThresholdConsecutiveCount, time.Now());
                Timeline = new TransmissionTimeline(settings.ClockJumpThreshold);
                log = new CollectorLog(settings.LogDirectory, Id);
                Responses = new ResponseObserver();
                client = new HttpServerClient(settings.ServerBaseUrl, settings.ApiKey, settings.HttpRequestTimeout,
                    time.Now, Responses);
                var runtime = new CollectorRuntime(settings, State, log,
                    device => new ModbusTcpClientReader(device.Host, device.Port, time.Now),
                    time.Now, time.Delay, id => () => 0, client, Timeline);
                StartedUtc = DateTime.UtcNow - time.Now();
                running = Task.Run(() => runtime.RunSupervisedAsync(cancellation.Token));
            }

            public Guid Id => State.RunId;
            public CollectorState State { get; }
            public CollectorSettings Settings { get; }
            public TransmissionTimeline Timeline { get; }
            private ResponseObserver Responses { get; }
            public DateTime StartedUtc { get; }
            public DateTime FirstSuccessUtc
            {
                get
                {
                    long ticks = Responses.FirstSuccessTicks;
                    Assert.True(ticks > 0, "No successful HTTP response was observed.");
                    return new DateTime(ticks, DateTimeKind.Utc);
                }
            }

            public Task<int> StopAsync()
            {
                return stopped ?? (stopped = StopCoreAsync());
            }

            private async Task<int> StopCoreAsync()
            {
                try
                {
                    cancellation.Cancel();
                    return await running;
                }
                finally
                {
                    client.Dispose();
                    log.Dispose();
                    cancellation.Dispose();
                }
            }

            public async Task AssertStoppedAsync()
            {
                int exitCode = await StopAsync();
                Assert.Equal(0, exitCode);
                Assert.Contains(Directory.GetFiles(Settings.LogDirectory, "*.log").SelectMany(File.ReadLines),
                    line => (string)JObject.Parse(line)["EventKind"] == "ShutdownCompleted");
            }

            public void AssertResponseKeysAbsent()
            {
                foreach (var body in Responses.Bodies)
                    AssertNoKeys(body);
            }
        }

        private sealed class ResponseObserver : DelegatingHandler
        {
            private long firstSuccessTicks;

            public ResponseObserver() : base(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            {
            }

            public ConcurrentQueue<string> Bodies { get; } = new ConcurrentQueue<string>();
            public long FirstSuccessTicks => Interlocked.Read(ref firstSuccessTicks);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var response = await base.SendAsync(request, cancellationToken);
                try
                {
                    var body = await response.Content.ReadAsStringAsync();
                    var receivedUtc = DateTime.UtcNow;
                    Bodies.Enqueue(body);
                    if (response.IsSuccessStatusCode)
                        Interlocked.CompareExchange(ref firstSuccessTicks, receivedUtc.Ticks, 0);
                    return response;
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }
        }

        private static void AssertNoKeys(string text)
        {
            Assert.DoesNotContain(CollectorKey, text);
            Assert.DoesNotContain(DashboardKey, text);
        }

        private sealed class SampleRow
        {
            public int EquipmentId { get; set; }
            public long SequenceNumber { get; set; }
            public DateTime CollectedAtUtc { get; set; }
            public float? Temperature { get; set; }
        }

        private sealed class AlarmEventRow
        {
            public long EventSequence { get; set; }
            public byte EventType { get; set; }
            public DateTime OccurredAtUtc { get; set; }
            public int? AlarmId { get; set; }
        }

        private sealed class AlarmRow
        {
            public int AlarmId { get; set; }
            public DateTime RaisedAtUtc { get; set; }
            public DateTime? ClearedAtUtc { get; set; }
        }
    }
}
