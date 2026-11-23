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
using Owin;
using Serilog;
using Serilog.Formatting.Compact;
using Server;
using Xunit;
using CollectorState = Collector.CollectorState;

namespace IntegrationTests
{
    internal sealed class Harness : IDisposable
    {
        private const string CollectorKey = "fake-collector-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string DashboardKey = "fake-dashboard-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const byte TemperatureCause = 2;
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

        public Server.CollectorState ServerCollectorState { get; private set; }

        public void SetDeviceDelay(int equipmentId, int milliseconds)
        {
            devices[equipmentId - 1].DelayMilliseconds = milliseconds;
        }

        public void StartServer(DelegatingHandler responseHandler = null)
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
            ServerCollectorState = new Server.CollectorState(time.Now,
                TimeSpan.FromSeconds(settings.CollectorStoppedSeconds));
            var runtime = new ApiRuntime(settings, ServerCollectorState, databaseMonitor, () => DateTime.UtcNow, serverLog);
            host = WebApp.Start(settings.ServerUrl, app =>
            {
                if (responseHandler == null)
                {
                    ApiConfiguration.Configure(app, runtime);
                    return;
                }

                var configuration = ApiConfiguration.Create(runtime);
                configuration.MessageHandlers.Add(responseHandler);
                app.UseWebApi(configuration);
            });
            databaseMonitor.Start();
        }

        public CollectorRun StartCollector(string serverBaseUrl = "https://localhost:5443",
            IReadOnlyDictionary<string, string> overrides = null, bool seedTimeReference = false,
            bool observeSampleHeaders = false)
        {
            var logDirectory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            var values = new Dictionary<string, string>
            {
                ["SERVER_BASE_URL"] = serverBaseUrl,
                ["COLLECTOR_API_KEY"] = CollectorKey,
                ["LogDirectory"] = logDirectory,
                ["PollIntervalMilliseconds"] = "400",
                ["ReadTimeoutMilliseconds"] = "350",
                ["HeartbeatIntervalMilliseconds"] = "250",
                ["ThresholdConsecutiveCount"] = "3"
            };
            for (int index = 0; index < devices.Length; index++)
                values[$"Equipment.{index + 1}.Port"] = devices[index].Port.ToString(CultureInfo.InvariantCulture);

            if (overrides != null)
                foreach (var setting in overrides)
                    values[setting.Key] = setting.Value;

            Assert.Empty(CollectorSettings.Validate(values, out var settings));
            var run = new CollectorRun(settings, seedTimeReference, observeSampleHeaders);
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

        internal static void AssertNoKeys(string text)
        {
            Assert.DoesNotContain(CollectorKey, text);
            Assert.DoesNotContain(DashboardKey, text);
        }
    }

    internal sealed class CollectorRun
    {
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly CollectorLog log;
        private readonly HttpServerClient client;
        private readonly Task<int> running;
        private Task<int> stopped;

        public CollectorRun(CollectorSettings settings, bool seedTimeReference, bool observeSampleHeaders)
        {
            Settings = settings;
            var time = CollectorTime.StartReal();
            State = new CollectorState(Guid.NewGuid(), settings.Equipment.Select(device => device.Id),
                settings.SampleBufferCapacity, settings.AlarmQueueCapacity,
                settings.CommunicationRecoverySuccessCount, settings.ThresholdConsecutiveCount, time.Now());
            Timeline = new TransmissionTimeline(settings.ClockJumpThreshold);
            log = new CollectorLog(settings.LogDirectory, Id);
            Responses = new ResponseObserver(observeSampleHeaders);
            client = new HttpServerClient(settings.ServerBaseUrl, settings.ApiKey, settings.HttpRequestTimeout,
                time.Now, Responses);
            var runtime = new CollectorRuntime(settings, State, log,
                device => new ModbusTcpClientReader(device.Host, device.Port, time.Now),
                time.Now, time.Delay, id => () => 0, client, Timeline);
            StartedUtc = DateTime.UtcNow - time.Now();
            if (seedTimeReference)
                Timeline.UpdateReference(TimeSpan.Zero, StartedUtc);
            running = Task.Run(() => runtime.RunSupervisedAsync(cancellation.Token));
        }

        public Guid Id => State.RunId;
        public CollectorState State { get; }
        public CollectorSettings Settings { get; }
        public TransmissionTimeline Timeline { get; }
        private ResponseObserver Responses { get; }
        public SampleRequestObservation[] SampleRequests => Responses.SampleRequests.ToArray();
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

        public async Task AssertStoppedAsync(int expectedExitCode = ExitCodes.Success)
        {
            int exitCode = await StopAsync();
            Assert.Equal(expectedExitCode, exitCode);
            string shutdownEvent = expectedExitCode == ExitCodes.Success ? "ShutdownCompleted" : "ShutdownIncomplete";
            Assert.Contains(Directory.GetFiles(Settings.LogDirectory, "*.log").SelectMany(File.ReadLines),
                line => (string)JObject.Parse(line)["EventKind"] == shutdownEvent);
        }

        public JObject[] ReadLogEntries()
        {
            if (!Directory.Exists(Settings.LogDirectory))
                return new JObject[0];

            var entries = new List<JObject>();
            foreach (var path in Directory.GetFiles(Settings.LogDirectory, "*.log"))
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                {
                    var snapshot = reader.ReadToEnd();
                    int completeLength = snapshot.LastIndexOf('\n') + 1;
                    using (var lines = new StringReader(snapshot.Substring(0, completeLength)))
                    {
                        string line;
                        while ((line = lines.ReadLine()) != null)
                            entries.Add(JObject.Parse(line));
                    }
                }
            }
            return entries.ToArray();
        }

        public void AssertResponseKeysAbsent()
        {
            foreach (var body in Responses.Bodies)
                Harness.AssertNoKeys(body);
        }
    }

    internal sealed class ResponseObserver : DelegatingHandler
    {
        private readonly bool observeSampleHeaders;
        private long firstSuccessTicks;

        public ResponseObserver(bool observeSampleHeaders)
            : base(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            this.observeSampleHeaders = observeSampleHeaders;
        }

        public ConcurrentQueue<string> Bodies { get; } = new ConcurrentQueue<string>();
        public ConcurrentQueue<SampleRequestObservation> SampleRequests { get; } =
            new ConcurrentQueue<SampleRequestObservation>();
        public long FirstSuccessTicks => Interlocked.Read(ref firstSuccessTicks);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (observeSampleHeaders && request.RequestUri.AbsolutePath == "/api/v1/collector/samples")
            {
                var observation = new SampleRequestObservation();
                SampleRequests.Enqueue(observation);
                var headers = await base.SendAsync(request, cancellationToken);
                if (headers.IsSuccessStatusCode)
                    observation.MarkSuccessHeaders();
                // Let HttpServerClient's ResponseContentRead buffer this body under its own timeout.
                return headers;
            }

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

    internal sealed class SampleRequestObservation
    {
        private long successHeaderTicks;

        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public DateTime SuccessHeadersUtc => new DateTime(Interlocked.Read(ref successHeaderTicks), DateTimeKind.Utc);

        public void MarkSuccessHeaders()
        {
            Interlocked.Exchange(ref successHeaderTicks, DateTime.UtcNow.Ticks);
        }
    }

    internal sealed class SampleRow
    {
        public int EquipmentId { get; set; }
        public long SequenceNumber { get; set; }
        public DateTime CollectedAtUtc { get; set; }
        public float? Temperature { get; set; }
    }

    internal sealed class AlarmEventRow
    {
        public long EventSequence { get; set; }
        public byte EventType { get; set; }
        public DateTime OccurredAtUtc { get; set; }
        public int? AlarmId { get; set; }
    }

    internal sealed class AlarmRow
    {
        public int AlarmId { get; set; }
        public DateTime RaisedAtUtc { get; set; }
        public DateTime? ClearedAtUtc { get; set; }
    }
}
