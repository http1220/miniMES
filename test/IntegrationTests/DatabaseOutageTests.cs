using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using Dapper;
using Data;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Formatting.Compact;
using Server;
using Xunit;
using Xunit.Abstractions;

namespace IntegrationTests
{
    [Collection("SharedLocalResources")]
    [Trait("Category", "Toxiproxy")]
    public sealed class DatabaseOutageTests : IDisposable
    {
        private const string CollectorKey = "fake-collector-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string DashboardKey = "fake-dashboard-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string ProxyDataSource = "tcp:[::1],21433";
        private const int UnavailableLimitMilliseconds = 20000;
        private const int ConnectedLimitMilliseconds = 15000;
        private const int PollIntervalMilliseconds = 200;
        private readonly string databaseName = "MiniMES_Outage_" + Guid.NewGuid().ToString("N");
        private readonly string directory = Path.Combine(Path.GetTempPath(), "database-outage-" + Guid.NewGuid().ToString("N"));
        private readonly SqlConnection admin;
        private readonly SqlConnection connection;
        private readonly string connectionString;
        private readonly string proxyConnection;
        private readonly ITestOutputHelper output;

        public DatabaseOutageTests(ITestOutputHelper output)
        {
            this.output = output;
            var configured = Environment.GetEnvironmentVariable("MINIMES_TEST_SQL_CONNECTION_STRING");
            Assert.False(string.IsNullOrWhiteSpace(configured), "Set MINIMES_TEST_SQL_CONNECTION_STRING for a test SQL Server with CREATE DATABASE permission.");
            var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master", Pooling = false };
            admin = new SqlConnection(builder.ConnectionString);
            admin.Open();
            admin.Execute("CREATE DATABASE [" + databaseName + "]");
            builder.InitialCatalog = databaseName;
            connectionString = builder.ConnectionString;
            proxyConnection = new SqlConnectionStringBuilder(configured) { InitialCatalog = databaseName, DataSource = ProxyDataSource }.ConnectionString;
            connection = new SqlConnection(connectionString);
            try
            {
                Directory.CreateDirectory(directory);
                connection.Open();
                MigrationRunner.Run(connectionString);
            }
            catch { Dispose(); throw; }
        }

        [Fact]
        public Task AlarmEvents_RecoversAfterDatabaseOutage()
        {
            var runId = Guid.NewGuid();
            var body = new JObject { ["runId"] = runId.ToString(), ["startedAtUtc"] = "2026-11-06T01:02:03.123Z",
                ["events"] = new JArray(new[] { 1, 2 }.Select((id, index) => new JObject {
                    ["eventSequence"] = index + 1, ["equipmentId"] = id, ["cause"] = 2,
                    ["eventType"] = 1, ["occurredAtUtc"] = "2026-11-06T01:02:04.123Z" })) };
            return ExerciseOutage("/api/v1/collector/alarm-events", "dbo.ALARM_EVENT", "appliedEvents", "duplicateEvents", runId, body);
        }

        [Fact]
        public Task Samples_RecoversAfterDatabaseOutage()
        {
            var runId = Guid.NewGuid();
            var body = new JObject { ["runId"] = runId.ToString(), ["startedAtUtc"] = "2026-11-06T01:02:03.123Z",
                ["samples"] = new JArray(new[] { 1, 1 }.Select((id, index) => new JObject {
                    ["sequenceNumber"] = index + 1, ["equipmentId"] = id,
                    ["temperature"] = index == 0 ? (JToken)new JValue(23.456) : JValue.CreateNull(),
                    ["vibration"] = index == 0 ? (JToken)new JValue(0.125) : JValue.CreateNull(),
                    ["statusCode"] = index == 0 ? (JToken)new JValue(1) : JValue.CreateNull(),
                    ["collectedAtUtc"] = "2026-11-06T01:02:04.123Z" })) };
            return ExerciseOutage("/api/v1/collector/samples", "dbo.SAMPLE", "storedSamples", "duplicateSamples", runId, body);
        }

        private async Task ExerciseOutage(string endpoint, string table, string storedField, string duplicateField, Guid runId, JObject body)
        {
            using (var proxy = new ProxyApi())
            {
                await proxy.CheckReady();
                try
                {
                    var logPath = Path.Combine(directory, "server.log");
                    try
                    {
                        using (var harness = new Harness(proxyConnection, logPath))
                        {
                            harness.Database.Start();
                            Assert.True(await WaitForConnected(harness), "proxy path did not connect within 15 seconds; see docs/fault-injection.md.");
                            await proxy.SetEnabled(false);
                            var elapsed = Stopwatch.StartNew();
                            var unavailable = await harness.Send(HttpMethod.Post, endpoint, CollectorKey, body, 503);
                            elapsed.Stop();
                            output.WriteLine("{0}: unavailable response after {1} ms", endpoint, elapsed.ElapsedMilliseconds);
                            Assert.True(elapsed.ElapsedMilliseconds <= UnavailableLimitMilliseconds, "Unavailable response exceeded 20 seconds.");
                            Assert.Equal("database_unavailable", (string)unavailable["error"]);
                            var status = await harness.Send(HttpMethod.Get, "/api/v1/status", DashboardKey, null, 200);
                            Assert.Equal("disconnected", (string)status["database"]);
                            await harness.Send(HttpMethod.Post, "/api/v1/collector/heartbeat", CollectorKey,
                                new JObject { ["runId"] = runId.ToString() }, 200);
                            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM " + table));
                            await proxy.SetEnabled(true);
                            elapsed.Restart();
                            var connected = await WaitForConnected(harness);
                            elapsed.Stop();
                            output.WriteLine("{0}: database connected after {1} ms", endpoint, elapsed.ElapsedMilliseconds);
                            Assert.True(connected && elapsed.ElapsedMilliseconds <= ConnectedLimitMilliseconds, "Database did not reconnect within 15 seconds.");
                            elapsed.Restart();
                            var replay = await harness.Send(HttpMethod.Post, endpoint, CollectorKey, body, 200, () => {
                                elapsed.Stop();
                                output.WriteLine("{0}: replay response after {1} ms", endpoint, elapsed.ElapsedMilliseconds);
                            });
                            Assert.Equal(2, (int)replay[storedField]);
                            Assert.Equal(0, (int)replay[duplicateField]);
                            Assert.Equal(2, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM " + table));
                            Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.COLLECTOR_RUN"));
                        }
                    }
                    finally { WriteLogSummary(logPath); }
                }
                finally
                {
                    try { await proxy.SetEnabled(true); }
                    catch (Exception) { }
                }
            }
        }

        private static async Task<bool> WaitForConnected(Harness harness)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds <= ConnectedLimitMilliseconds)
            {
                var status = await harness.Send(HttpMethod.Get, "/api/v1/status", DashboardKey, null, 200);
                if ((string)status["database"] == "connected") return elapsed.ElapsedMilliseconds <= ConnectedLimitMilliseconds;
                await Task.Delay(PollIntervalMilliseconds);
            }
            return false;
        }

        private sealed class ProxyApi : IDisposable
        {
            private const string ProxyName = "minimes-sql";
            private readonly HttpClient client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8474/"), Timeout = TimeSpan.FromSeconds(10) };

            public ProxyApi() { client.DefaultRequestHeaders.UserAgent.ParseAdd("minimes-tests"); }

            public async Task CheckReady()
            {
                HttpResponseMessage response = null;
                try { response = await client.GetAsync("proxies/" + ProxyName); }
                catch (HttpRequestException) { Assert.True(false, "Toxiproxy API not reachable at 127.0.0.1:8474; see docs/fault-injection.md."); }
                catch (TaskCanceledException) { Assert.True(false, "Toxiproxy API not reachable at 127.0.0.1:8474 (timeout); see docs/fault-injection.md."); }
                using (response)
                {
                    Assert.True(response.StatusCode != HttpStatusCode.NotFound, "Proxy minimes-sql is missing; create it as described in docs/fault-injection.md.");
                    Assert.True(response.IsSuccessStatusCode, "Toxiproxy proxy lookup failed: " + (int)response.StatusCode + "; see docs/fault-injection.md.");
                    var proxy = JObject.Parse(await response.Content.ReadAsStringAsync());
                    Assert.True((string)proxy["name"] == ProxyName, "Unexpected proxy name; see docs/fault-injection.md.");
                    Assert.True((string)proxy["listen"] == "[::1]:21433", "Unexpected listen: " + (string)proxy["listen"] + "; expected [::1]:21433; see docs/fault-injection.md.");
                    Assert.True((string)proxy["upstream"] == "127.0.0.1:1433", "Unexpected upstream: " + (string)proxy["upstream"] + "; expected 127.0.0.1:1433; see docs/fault-injection.md.");
                    Assert.True((bool?)proxy["enabled"] == true, "Proxy is disabled, possibly because a previous run was forcibly terminated; enable it as described in docs/fault-injection.md.");
                }
            }

            public async Task SetEnabled(bool enabled)
            {
                using (var content = new StringContent(new JObject { ["enabled"] = enabled }.ToString(), Encoding.UTF8, "application/json"))
                using (var response = await client.PostAsync("proxies/" + ProxyName, content))
                    response.EnsureSuccessStatusCode();
            }

            public void Dispose() { client.Dispose(); }
        }

        private sealed class Harness : IDisposable
        {
            public readonly DatabaseMonitor Database;
            private readonly Serilog.Core.Logger logger;
            private readonly HttpConfiguration configuration;
            private readonly HttpClient client;

            public Harness(string selectedConnection, string logPath)
            {
                ServerSettings settings;
                Assert.Empty(ServerSettings.Validate(new Dictionary<string, string> {
                    ["SERVER_URL"] = "https://+:5443/", ["DB_CONNECTION_STRING"] = selectedConnection,
                    ["COLLECTOR_API_KEY"] = CollectorKey, ["DASHBOARD_API_KEY"] = DashboardKey }, out settings));
                logger = new LoggerConfiguration().WriteTo.File(new CompactJsonFormatter(), logPath).CreateLogger();
                var probe = new DatabaseProbe(selectedConnection, settings.ProbeTimeoutSeconds);
                Database = new DatabaseMonitor(() => probe.Check(), TimeSpan.FromSeconds(settings.ProbeIntervalSeconds));
                configuration = ApiConfiguration.Create(new ApiRuntime(settings,
                    new CollectorState(() => TimeSpan.Zero, TimeSpan.FromSeconds(10)), Database, () => DateTime.UtcNow, logger));
                client = new HttpClient(new HttpServer(configuration)) { BaseAddress = new Uri("http://localhost/") };
            }

            public async Task<JObject> Send(HttpMethod method, string path, string key, JObject body, int expectedStatus, Action onResponse = null)
            {
                using (var request = new HttpRequestMessage(method, path))
                {
                    request.Headers.Add("X-Api-Key", key);
                    if (body != null) request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
                    using (var response = await client.SendAsync(request))
                    {
                        if (onResponse != null) onResponse();
                        Assert.Equal(expectedStatus, (int)response.StatusCode);
                        var text = await response.Content.ReadAsStringAsync();
                        Assert.DoesNotContain(CollectorKey, text);
                        Assert.DoesNotContain(DashboardKey, text);
                        return JObject.Parse(text);
                    }
                }
            }

            public void Dispose()
            {
                client.Dispose();
                configuration.Dispose();
                Database.Dispose();
                logger.Dispose();
            }
        }

        private void WriteLogSummary(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                foreach (var line in File.ReadLines(path))
                {
                    try
                    {
                        var entry = JObject.Parse(line);
                        output.WriteLine("log: {0} {1} {2} {3} sql={4} type={5}",
                            (string)entry["@t"], (string)entry["@l"] ?? "Information", (string)entry["Endpoint"],
                            (string)entry["Error"], (string)entry["SqlNumber"], (string)entry["ExceptionType"]);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        private void AssertLogsContainNoSecrets()
        {
            foreach (var path in Directory.GetFiles(directory, "*.log"))
            {
                var text = File.ReadAllText(path);
                Assert.DoesNotContain(CollectorKey, text);
                Assert.DoesNotContain(DashboardKey, text);
                Assert.DoesNotContain(connectionString, text);
                Assert.DoesNotContain(proxyConnection, text);
            }
        }

        public void Dispose()
        {
            try { if (Directory.Exists(directory)) AssertLogsContainNoSecrets(); }
            finally
            {
                connection.Dispose();
                try
                {
                    admin.Execute("ALTER DATABASE [" + databaseName + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
                    admin.Execute("DROP DATABASE [" + databaseName + "]");
                }
                finally
                {
                    admin.Dispose();
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            }
        }
    }
}
