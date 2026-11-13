using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Formatting.Compact;
using Server;
using Xunit;

namespace UnitTests
{
    public sealed class ServerSkeletonTests
    {
        private const string CollectorKey = "test-collector-key-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string DashboardKey = "test-dashboard-key-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private static readonly DateTime Now = new DateTime(2026, 11, 13, 1, 2, 3, 456, DateTimeKind.Utc);

        private static Dictionary<string, string> Values()
        {
            return new Dictionary<string, string>
            {
                ["SERVER_URL"] = "https://+:5443/",
                ["DB_CONNECTION_STRING"] = "Server=example.invalid;Database=fake;Integrated Security=True",
                ["COLLECTOR_API_KEY"] = CollectorKey,
                ["DASHBOARD_API_KEY"] = DashboardKey
            };
        }

        private static ServerSettings Settings()
        {
            ServerSettings settings;
            Assert.Empty(ServerSettings.Validate(Values(), out settings));
            return settings;
        }

        private sealed class Harness : IDisposable
        {
            public readonly DatabaseMonitor Database;
            public readonly CollectorState Collector;
            public readonly HttpClient Client;
            private readonly HttpConfiguration configuration;

            public Harness(ILogger logger, Func<DateTime> clock = null, Func<TimeSpan> monotonic = null)
            {
                Database = new DatabaseMonitor(() => { throw new InvalidOperationException(); }, TimeSpan.FromSeconds(2));
                Collector = new CollectorState(monotonic ?? (() => TimeSpan.Zero), TimeSpan.FromSeconds(10));
                configuration = ApiConfiguration.Create(new ApiRuntime(Settings(), Collector, Database, clock ?? (() => Now), logger));
                Client = new HttpClient(new HttpServer(configuration)) { BaseAddress = new Uri("http://localhost/") };
            }

            public async Task<HttpResponseMessage> Send(bool heartbeat, string key, string body = null)
            {
                var request = new HttpRequestMessage(heartbeat ? HttpMethod.Post : HttpMethod.Get,
                    heartbeat ? "api/v1/collector/heartbeat" : "api/v1/status");
                if (key != null) request.Headers.TryAddWithoutValidation("X-Api-Key", key);
                if (heartbeat) request.Content = new StringContent(body ?? "{\"runId\":\"11111111-1111-1111-1111-111111111111\"}", Encoding.UTF8, "application/json");
                using (request) return await Client.SendAsync(request);
            }

            public void Dispose() { Client.Dispose(); configuration.Dispose(); Database.Dispose(); }
        }

        [Fact]
        public async Task AuthorizationMatrix_RejectsWrongKeys_AndNeverDisclosesKeys()
        {
            var directory = Path.Combine(Path.GetTempPath(), "server-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var logPath = Path.Combine(directory, "auth.log");
            try
            {
                using (var logger = new LoggerConfiguration().WriteTo.File(new CompactJsonFormatter(), logPath).CreateLogger())
                using (var harness = new Harness(logger))
                {
                    foreach (var heartbeat in new[] { false, true })
                    foreach (var key in new[] { null, "", "test-invalid-key-cccccccccccccccccccccccccccccccc", CollectorKey, DashboardKey })
                    {
                        var expected = key == CollectorKey ? (heartbeat ? 200 : 403) :
                            key == DashboardKey ? (heartbeat ? 403 : 200) : 401;
                        using (var response = await harness.Send(heartbeat, key))
                        {
                            Assert.Equal(expected, (int)response.StatusCode);
                            var text = await response.Content.ReadAsStringAsync();
                            Assert.DoesNotContain(CollectorKey, text);
                            Assert.DoesNotContain(DashboardKey, text);
                            Assert.DoesNotContain("test-invalid-key-cccccccccccccccccccccccccccccccc", text);
                            if (expected != 200) Assert.Equal(expected == 401 ? "unauthorized" : "forbidden", (string)JObject.Parse(text)["error"]);
                        }
                    }
                }
                var logs = File.ReadAllText(logPath);
                Assert.Contains("missing", logs);
                Assert.Contains("invalid", logs);
                Assert.Contains("forbidden", logs);
                Assert.DoesNotContain(CollectorKey, logs);
                Assert.DoesNotContain(DashboardKey, logs);
                Assert.DoesNotContain("test-invalid-key-cccccccccccccccccccccccccccccccc", logs);
            }
            finally { Directory.Delete(directory, true); }
        }

        [Fact]
        public void EveryControllerAction_HasExactlyOneKeyAttribute()
        {
            var controllers = typeof(ApiConfiguration).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(ApiController).IsAssignableFrom(t)).ToArray();
            Assert.NotEmpty(controllers);
            foreach (var controller in controllers)
            foreach (var action in controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && !m.IsDefined(typeof(NonActionAttribute), true)))
            {
                Assert.Single(action.GetCustomAttributes(typeof(ApiKeyAttribute), true)
                    .Concat(controller.GetCustomAttributes(typeof(ApiKeyAttribute), true)));
            }
        }

        [Theory]
        [InlineData("not json", "invalid_json")]
        [InlineData("{", "invalid_json")]
        [InlineData("{}", "invalid_field")]
        [InlineData("null", "invalid_field")]
        [InlineData("{\"runId\":\"bad\"}", "invalid_field")]
        [InlineData("{\"runId\":42}", "invalid_field")]
        [InlineData("{\"runId\":\"00000000-0000-0000-0000-000000000000\"}", "invalid_field")]
        public async Task Heartbeat_ValidatesBody(string body, string error)
        {
            using (var logger = new LoggerConfiguration().CreateLogger())
            using (var harness = new Harness(logger))
            using (var response = await harness.Send(true, CollectorKey, body))
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal(error, (string)JObject.Parse(await response.Content.ReadAsStringAsync())["error"]);
                Assert.Null(harness.Collector.Read().LastHeartbeatUtc);
            }
        }

        [Fact]
        public async Task Collector_UsesMonotonicClock_AndRecordsServerUtc()
        {
            var elapsed = TimeSpan.Zero;
            using (var logger = new LoggerConfiguration().CreateLogger())
            using (var harness = new Harness(logger, monotonic: () => elapsed))
            {
                Assert.Equal("checking", harness.Collector.Read().State);
                elapsed = TimeSpan.FromSeconds(10);
                Assert.Equal("stopped", harness.Collector.Read().State);
                using (var response = await harness.Send(true, CollectorKey)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("running", harness.Collector.Read().State);
                Assert.Equal(Now, harness.Collector.Read().LastHeartbeatUtc);
                elapsed += TimeSpan.FromSeconds(9.999);
                Assert.Equal("running", harness.Collector.Read().State);
                elapsed += TimeSpan.FromMilliseconds(1);
                Assert.Equal("stopped", harness.Collector.Read().State);
                using (var response = await harness.Send(true, CollectorKey)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("running", harness.Collector.Read().State);
            }
        }

        [Fact]
        public async Task Status_HasFourCamelCaseFields_AndUtcMilliseconds()
        {
            using (var logger = new LoggerConfiguration().CreateLogger())
            using (var harness = new Harness(logger))
            using (var response = await harness.Send(false, DashboardKey))
            {
                var text = await response.Content.ReadAsStringAsync();
                var body = JObject.Parse(text);
                Assert.Equal(new[] { "serverTimeUtc", "database", "collector", "lastHeartbeatUtc" }, body.Properties().Select(p => p.Name));
                Assert.Contains("2026-11-13T01:02:03.456Z", text);
                Assert.Equal(JTokenType.Null, body["lastHeartbeatUtc"].Type);
                Assert.Equal("checking", (string)body["database"]);
                Assert.Equal("checking", (string)body["collector"]);
                using (var heartbeat = await harness.Send(true, CollectorKey)) Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
                using (var next = await harness.Send(false, DashboardKey))
                    Assert.Contains("\"lastHeartbeatUtc\":\"2026-11-13T01:02:03.456Z\"", await next.Content.ReadAsStringAsync());
            }
        }

        [Fact]
        public async Task UnhandledException_ReturnsSafeInternalError()
        {
            using (var logger = new LoggerConfiguration().CreateLogger())
            using (var harness = new Harness(logger, () => { throw new InvalidOperationException(CollectorKey); }))
            using (var response = await harness.Send(false, DashboardKey))
            {
                Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
                Assert.Equal("{\"error\":\"internal\"}", await response.Content.ReadAsStringAsync());
            }
        }

        [Fact]
        public async Task DatabaseMonitor_RecoversWithoutOverlappingOrContinuingAfterSuccess()
        {
            var calls = 0;
            var active = 0;
            var maximum = 0;
            var succeed = 0;
            using (var entered = new SemaphoreSlim(0))
            using (var release = new SemaphoreSlim(0))
            using (var monitor = new DatabaseMonitor(() =>
            {
                var count = Interlocked.Increment(ref active);
                maximum = Math.Max(maximum, count);
                Interlocked.Increment(ref calls);
                entered.Release();
                try { Assert.True(release.Wait(TimeSpan.FromSeconds(5))); return Volatile.Read(ref succeed) == 1; }
                finally { Interlocked.Decrement(ref active); }
            }, TimeSpan.FromMilliseconds(15)))
            {
                Assert.Equal("checking", monitor.State);
                monitor.Start();
                Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));
                release.Release();
                Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal("disconnected", monitor.State);
                release.Release();
                Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));
                Volatile.Write(ref succeed, 1);
                release.Release();
                await WaitFor(() => monitor.State == "connected");
                var stoppedCalls = Volatile.Read(ref calls);
                await Task.Delay(60);
                Assert.Equal(stoppedCalls, Volatile.Read(ref calls));
                Volatile.Write(ref succeed, 0);
                monitor.MarkDisconnected();
                Assert.Equal("disconnected", monitor.State);
                Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));
                for (var i = 0; i < 20; i++) monitor.MarkDisconnected();
                release.Release();
                Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal("disconnected", monitor.State);
                Volatile.Write(ref succeed, 1);
                release.Release();
                await WaitFor(() => monitor.State == "connected");
                stoppedCalls = Volatile.Read(ref calls);
                await Task.Delay(60);
                Assert.Equal(stoppedCalls, Volatile.Read(ref calls));
                Assert.Equal(1, maximum);
            }
        }

        [Fact]
        public async Task DatabaseMonitor_InitialSuccess_AndCancellationStopProbing()
        {
            var calls = 0;
            using (var monitor = new DatabaseMonitor(() => { Interlocked.Increment(ref calls); return true; }, TimeSpan.FromMilliseconds(10)))
            {
                monitor.Start();
                await WaitFor(() => monitor.State == "connected");
                await Task.Delay(40);
                Assert.Equal(1, calls);
            }
            var failed = new DatabaseMonitor(() => { Interlocked.Increment(ref calls); return false; }, TimeSpan.FromMilliseconds(10));
            failed.Start();
            await WaitFor(() => failed.State == "disconnected");
            failed.Dispose();
            var before = Volatile.Read(ref calls);
            await Task.Delay(40);
            Assert.Equal(before, Volatile.Read(ref calls));
        }

        private static async Task WaitFor(Func<bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!predicate())
            {
                Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for monitor state.");
                await Task.Delay(5);
            }
        }

        [Theory]
        [InlineData("SERVER_URL")]
        [InlineData("DB_CONNECTION_STRING")]
        [InlineData("COLLECTOR_API_KEY")]
        [InlineData("DASHBOARD_API_KEY")]
        public void Settings_RequireEachMandatoryKey(string key)
        {
            var values = Values();
            values.Remove(key);
            ServerSettings settings;
            var errors = ServerSettings.Validate(values, out settings);
            Assert.Single(errors);
            Assert.Contains(key, errors[0]);
        }

        [Theory]
        [InlineData("SERVER_URL", "http://invalid.example/")]
        [InlineData("COLLECTOR_API_KEY", "1234567890123456789012345678901")]
        [InlineData("DASHBOARD_API_KEY", "1234567890123456789012345678901")]
        [InlineData("DB_CONNECTION_STRING", "invalid-connection-fragment")]
        [InlineData("DB_PROBE_TIMEOUT_SECONDS", "0")]
        [InlineData("DB_PROBE_TIMEOUT_SECONDS", "-1")]
        [InlineData("DB_PROBE_TIMEOUT_SECONDS", "invalid-number")]
        [InlineData("DB_PROBE_INTERVAL_SECONDS", "0")]
        [InlineData("DB_PROBE_INTERVAL_SECONDS", "-1")]
        [InlineData("DB_PROBE_INTERVAL_SECONDS", "invalid-number")]
        [InlineData("COLLECTOR_STOPPED_SECONDS", "0")]
        [InlineData("COLLECTOR_STOPPED_SECONDS", "-1")]
        [InlineData("COLLECTOR_STOPPED_SECONDS", "invalid-number")]
        public void Settings_RejectInvalidValuesWithoutEchoingThem(string key, string value)
        {
            var values = Values();
            values[key] = value;
            ServerSettings settings;
            var errors = ServerSettings.Validate(values, out settings);
            Assert.Single(errors);
            Assert.Contains(key, errors[0]);
            Assert.DoesNotContain(value, errors[0]);
        }

        [Fact]
        public void Settings_CollectAllViolations_AndValidateMigrationIndependently()
        {
            var values = Values();
            values["DASHBOARD_API_KEY"] = CollectorKey;
            ServerSettings settings;
            Assert.Single(ServerSettings.Validate(values, out settings));
            values["SERVER_URL"] = "http://invalid.example/";
            values["DB_CONNECTION_STRING"] = "invalid-connection-fragment";
            values["DB_PROBE_TIMEOUT_SECONDS"] = "invalid-number";
            var errors = ServerSettings.Validate(values, out settings);
            Assert.Equal(4, errors.Count);
            foreach (var value in values.Values)
                foreach (var error in errors) Assert.DoesNotContain(value, error);
            Assert.Equal(4, ServerSettings.Validate(new Dictionary<string, string>(), out settings).Count);
            var migration = new Dictionary<string, string> { ["DB_CONNECTION_STRING"] = Values()["DB_CONNECTION_STRING"] };
            Assert.Empty(ServerSettings.Validate(migration, out settings, true));
            Assert.Single(ServerSettings.Validate(new Dictionary<string, string>(), out settings, true));
            Assert.Empty(ServerSettings.Validate(Values(), out settings));
            Assert.Equal(2, settings.ProbeTimeoutSeconds);
            Assert.Equal(2, settings.ProbeIntervalSeconds);
            Assert.Equal(10, settings.CollectorStoppedSeconds);
        }

        [Fact]
        public void LogDirectory_DefaultsToLogsUnderBaseDirectory()
        {
            var baseDirectory = Path.Combine(Path.GetTempPath(), "server-tests-" + Guid.NewGuid().ToString("N"));
            var values = new Dictionary<string, string>();
            Assert.Equal(Path.Combine(baseDirectory, "logs"), ServerSettings.ResolveLogDirectory(values, baseDirectory));
            foreach (var value in new[] { "", "   " })
            {
                values["LOG_DIRECTORY"] = value;
                Assert.Equal(Path.Combine(baseDirectory, "logs"), ServerSettings.ResolveLogDirectory(values, baseDirectory));
            }
        }

        [Fact]
        public void LogDirectory_ResolvesRelativePathAgainstBaseDirectory()
        {
            var baseDirectory = Path.Combine(Path.GetTempPath(), "server-tests-" + Guid.NewGuid().ToString("N"));
            Assert.NotEqual(Path.GetFullPath(Directory.GetCurrentDirectory()), Path.GetFullPath(baseDirectory));
            foreach (var value in new[] { "logs2", Path.Combine("..", "x") })
            {
                var values = new Dictionary<string, string> { ["LOG_DIRECTORY"] = value };
                Assert.Equal(Path.GetFullPath(Path.Combine(baseDirectory, value)),
                    ServerSettings.ResolveLogDirectory(values, baseDirectory));
            }
        }

        [Fact]
        public void LogDirectory_KeepsAbsolutePath()
        {
            var directory = Path.Combine(Path.GetTempPath(), "server-tests-" + Guid.NewGuid().ToString("N"), "..", "logs2");
            var values = new Dictionary<string, string> { ["LOG_DIRECTORY"] = directory };
            Assert.Equal(Path.GetFullPath(directory),
                ServerSettings.ResolveLogDirectory(values, AppDomain.CurrentDomain.BaseDirectory));
        }

        [Fact]
        public void Settings_MigrationOnly_ResolvesLogDirectory()
        {
            var values = new Dictionary<string, string> { ["DB_CONNECTION_STRING"] = Values()["DB_CONNECTION_STRING"] };
            ServerSettings settings;
            Assert.Empty(ServerSettings.Validate(values, out settings, migrationOnly: true));
            Assert.Equal(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"), settings.LogDirectory);
            values["LOG_DIRECTORY"] = "logs2";
            Assert.Empty(ServerSettings.Validate(values, out settings, migrationOnly: true));
            Assert.Equal(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs2")), settings.LogDirectory);
        }

        [Fact]
        public void EnvParser_TrimsAndSplitsOnlyAtFirstEquals()
        {
            var values = ServerSettings.ParseEnv(new[] { "", "  ", " # comment", " ignored", " KEY = a=b=c ", " OTHER = value ", " QUOTED = \"literal\" " });
            Assert.Equal(3, values.Count);
            Assert.Equal("a=b=c", values["KEY"]);
            Assert.Equal("value", values["OTHER"]);
            Assert.Equal("\"literal\"", values["QUOTED"]);
        }
    }
}
