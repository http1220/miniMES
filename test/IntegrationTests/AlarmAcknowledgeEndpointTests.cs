using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using Dapper;
using Data;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Formatting.Compact;
using Server;
using Xunit;

namespace IntegrationTests
{
    public sealed class AlarmAcknowledgeEndpointTests : IDisposable
    {
        private const string CollectorKey = "fake-collector-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string DashboardKey = "fake-dashboard-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private static readonly DateTime Now = new DateTime(2026, 11, 13, 1, 2, 3, 456, DateTimeKind.Utc).AddTicks(7000);
        private readonly string databaseName = "MiniMES_Ack_" + Guid.NewGuid().ToString("N");
        private readonly string directory = Path.Combine(Path.GetTempPath(), "ack-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> usedConnections = new List<string>();
        private readonly SqlConnection admin;
        private readonly SqlConnection connection;
        private readonly string connectionString;
        private readonly int[] users;
        private readonly int alarmId;

        public AlarmAcknowledgeEndpointTests()
        {
            var configured = Environment.GetEnvironmentVariable("MINIMES_TEST_SQL_CONNECTION_STRING");
            Assert.False(string.IsNullOrWhiteSpace(configured));
            var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master", Pooling = false };
            admin = new SqlConnection(builder.ConnectionString);
            admin.Open();
            admin.Execute("CREATE DATABASE [" + databaseName + "]");
            builder.InitialCatalog = databaseName;
            connectionString = builder.ConnectionString;
            connection = new SqlConnection(connectionString);
            try
            {
                Directory.CreateDirectory(directory);
                connection.Open();
                MigrationRunner.Run(connectionString);
                users = connection.Query<int>("INSERT INTO dbo.APP_USER (Name) OUTPUT INSERTED.UserId VALUES (N'A'), (N'B')").ToArray();
                alarmId = connection.QuerySingle<int>(@"INSERT INTO dbo.ALARM (EquipmentId, Cause, RaisedAtUtc)
OUTPUT INSERTED.AlarmId VALUES (1, 1, @now)", new { now = Now });
            }
            catch { Dispose(); throw; }
        }

        private string PathFor(string id) { return "/api/v1/alarms/" + id + "/acknowledge"; }
        private string Body(int user) { return "{\"userId\":" + user + "}"; }
        private Harness CreateHarness(string selected = null)
        {
            selected = selected ?? connectionString;
            usedConnections.Add(selected);
            return new Harness(selected, Path.Combine(directory, Guid.NewGuid().ToString("N") + ".log"));
        }
        private void AssertStored(int? user, DateTime? time)
        {
            var row = connection.QuerySingle<AlarmRow>("SELECT AcknowledgedBy, AcknowledgedAtUtc FROM dbo.ALARM WHERE AlarmId=@alarmId", new { alarmId });
            Assert.Equal(user, row.AcknowledgedBy);
            Assert.Equal(time, row.AcknowledgedAtUtc);
        }
        private sealed class AlarmRow { public int? AcknowledgedBy { get; set; } public DateTime? AcknowledgedAtUtc { get; set; } }

        [Fact]
        public async Task Acknowledge_UnacknowledgedThenRepeated_PreservesFirstUserAndTime()
        {
            string path;
            using (var h = CreateHarness())
            {
                path = h.LogPath;
                var first = await h.Send(HttpMethod.Post, PathFor(alarmId.ToString()), DashboardKey, Body(users[0]), 200);
                Assert.Equal(users[0], (int)first["acknowledgedBy"]);
                Assert.Equal("2026-11-13T01:02:03.456Z", (string)first["acknowledgedAtUtc"]);
                Assert.Equal((string)first["acknowledgedAtUtc"], (string)first["serverTimeUtc"]);
                AssertStored(users[0], new DateTime(2026, 11, 13, 1, 2, 3, 456));
                var second = await h.Send(HttpMethod.Post, PathFor(alarmId.ToString()), DashboardKey, Body(users[1]), 409);
                Assert.Equal("already_acknowledged", (string)second["error"]);
                Assert.Equal(users[0], (int)second["acknowledgedBy"]);
                AssertStored(users[0], new DateTime(2026, 11, 13, 1, 2, 3, 456));
            }
            var logs = Logs(path);
            Assert.Single(logs.Where(x => x["@l"] == null));
            Assert.Equal("/api/v1/alarms/{alarmId}/acknowledge", (string)logs[0]["Endpoint"]);
        }

        [Fact]
        public async Task Acknowledge_MissingAlarm_ReturnsNotFound()
        {
            using (var h = CreateHarness())
                Assert.Equal("not_found", (string)(await h.Send(HttpMethod.Post, PathFor((alarmId + 1000).ToString()), DashboardKey, Body(users[0]), 404))["error"]);
        }

        [Fact]
        public async Task Acknowledge_MissingOrNonpositiveUser_ReturnsUserRequired()
        {
            using (var h = CreateHarness())
                foreach (var body in new[] { "{}", "{\"userId\":null}", "{\"userId\":0}", "{\"userId\":-1}" })
                    Assert.Equal("user_required", (string)(await h.Send(HttpMethod.Post, PathFor(alarmId.ToString()), DashboardKey, body, 400))["error"]);
            AssertStored(null, null);
        }

        [Fact]
        public async Task Acknowledge_InvalidFieldTypesOrPaths_ReturnsInvalidField()
        {
            using (var h = CreateHarness())
            {
                foreach (var body in new[] { "{\"userId\":\"1\"}", "{\"userId\":1.5}", "{\"userId\":true}", "{\"userId\":2147483648}", "[]", "\"x\"" })
                    Assert.Equal("invalid_field", (string)(await h.Send(HttpMethod.Post, PathFor(alarmId.ToString()), DashboardKey, body, 400))["error"]);
                foreach (var id in new[] { "abc", "0", "-1", "+1", "2147483648" })
                    Assert.Equal("invalid_field", (string)(await h.Send(HttpMethod.Post, PathFor(id), DashboardKey, Body(users[0]), 400))["error"]);
            }
            AssertStored(null, null);
        }

        [Fact]
        public async Task Acknowledge_InvalidPathOrMissingUser_UsesValidationOrder()
        {
            using (var h = CreateHarness())
            {
                Assert.Equal("invalid_field", (string)(await h.Send(HttpMethod.Post, PathFor("abc"), DashboardKey, "{}", 400))["error"]);
                Assert.Equal("user_required", (string)(await h.Send(HttpMethod.Post, PathFor((alarmId + 1000).ToString()), DashboardKey, "{}", 400))["error"]);
            }
        }

        [Fact]
        public async Task Acknowledge_InvalidJson_ReturnsInvalidJson()
        {
            using (var h = CreateHarness())
                foreach (var body in new[] { "{", "", "{\"userId\":1} {}" })
                    Assert.Equal("invalid_json", (string)(await h.Send(HttpMethod.Post, PathFor(alarmId.ToString()), DashboardKey, body, 400))["error"]);
        }

        [Fact]
        public async Task Acknowledge_ContentLengthBoundary_AcceptsLimitAndRejectsExcess()
        {
            using (var h = CreateHarness())
            {
                var body = Body(users[0]);
                Assert.Equal("invalid_field", (string)(await h.Send(HttpMethod.Post, PathFor(alarmId.ToString()), DashboardKey, body.PadRight(65537), 400))["error"]);
                AssertStored(null, null);
                await h.Send(HttpMethod.Post, PathFor(alarmId.ToString()), DashboardKey, body.PadRight(65536), 200);
                AssertStored(users[0], new DateTime(2026, 11, 13, 1, 2, 3, 456));
            }
        }

        [Fact]
        public async Task Acknowledge_UnknownUser_UsesDatabaseOutcomeOrder()
        {
            string path;
            using (var h = CreateHarness())
            {
                path = h.LogPath;
                Assert.Equal("invalid_field", (string)(await h.Send(HttpMethod.Post, PathFor(alarmId.ToString()), DashboardKey, Body(99999), 400))["error"]);
                AssertStored(null, null);
                await h.Send(HttpMethod.Post, PathFor(alarmId.ToString()), DashboardKey, Body(users[0]), 200);
                Assert.Equal("already_acknowledged", (string)(await h.Send(HttpMethod.Post, PathFor(alarmId.ToString()), DashboardKey, Body(99999), 409))["error"]);
                Assert.Equal("not_found", (string)(await h.Send(HttpMethod.Post, PathFor((alarmId + 1000).ToString()), DashboardKey, Body(99999), 404))["error"]);
            }
            var log = Logs(path)[0];
            Assert.Equal("Warning", (string)log["@l"]);
            Assert.Equal(547, (int)log["SqlNumber"]);
        }

        [Fact]
        public async Task Acknowledge_UnreachableDatabase_ReturnsUnavailableAndMarksDisconnected()
        {
            var builder = new SqlConnectionStringBuilder(connectionString) { DataSource = "tcp:127.0.0.1,1", ConnectTimeout = 2 };
            string path;
            using (var h = CreateHarness(builder.ConnectionString))
            {
                path = h.LogPath;
                Assert.Equal("database_unavailable", (string)(await h.Send(HttpMethod.Post, PathFor(alarmId.ToString()), DashboardKey, Body(users[0]), 503))["error"]);
                Assert.Equal("disconnected", h.Database.State);
            }
            Assert.Equal("Error", (string)Assert.Single(Logs(path))["@l"]);
        }

        [Fact]
        public async Task Acknowledge_KeyMatrix_EnforcesAuthorizationAndHidesSecrets()
        {
            string path;
            using (var h = CreateHarness())
            {
                path = h.LogPath;
                foreach (var key in new[] { null, "wrong-key" })
                    Assert.Equal("unauthorized", (string)(await h.Send(HttpMethod.Post, PathFor(alarmId.ToString()), key, Body(users[0]), 401))["error"]);
                Assert.Equal("forbidden", (string)(await h.Send(HttpMethod.Post, PathFor(alarmId.ToString()), CollectorKey, Body(users[0]), 403))["error"]);
                AssertStored(null, null);
                await h.Send(HttpMethod.Post, PathFor(alarmId.ToString()), DashboardKey, Body(users[0]), 200);
            }
            Assert.Equal(4, Logs(path).Length);
            AssertLogsContainNoSecrets();
        }

        private static JObject[] Logs(string path) { return File.ReadAllLines(path).Where(x => !string.IsNullOrWhiteSpace(x)).Select(JObject.Parse).ToArray(); }
        private void AssertLogsContainNoSecrets()
        {
            foreach (var path in Directory.GetFiles(directory, "*.log"))
            {
                var text = File.ReadAllText(path);
                Assert.DoesNotContain(CollectorKey, text);
                Assert.DoesNotContain(DashboardKey, text);
                foreach (var value in usedConnections) Assert.DoesNotContain(value, text);
            }
        }
        private sealed class Harness : IDisposable
        {
            public readonly string LogPath;
            public readonly DatabaseMonitor Database;
            private readonly Serilog.Core.Logger logger;
            private readonly HttpConfiguration configuration;
            private readonly HttpClient client;
            public Harness(string connectionString, string logPath)
            {
                LogPath = logPath;
                ServerSettings settings;
                Assert.Empty(ServerSettings.Validate(new Dictionary<string, string> { ["SERVER_URL"] = "https://+:5443/", ["DB_CONNECTION_STRING"] = connectionString, ["COLLECTOR_API_KEY"] = CollectorKey, ["DASHBOARD_API_KEY"] = DashboardKey }, out settings));
                logger = new LoggerConfiguration().WriteTo.File(new CompactJsonFormatter(), logPath).CreateLogger();
                Database = new DatabaseMonitor(() => false, TimeSpan.FromSeconds(2));
                configuration = ApiConfiguration.Create(new ApiRuntime(settings, new CollectorState(() => TimeSpan.Zero, TimeSpan.FromSeconds(10)), Database, () => Now, logger));
                client = new HttpClient(new HttpServer(configuration)) { BaseAddress = new Uri("http://localhost/") };
            }
            public async Task<JObject> Send(HttpMethod method, string path, string key, string body, int status)
            {
                using (var request = new HttpRequestMessage(method, path))
                {
                    if (key != null) request.Headers.Add("X-Api-Key", key);
                    if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var response = await client.SendAsync(request))
                    {
                        Assert.Equal(status, (int)response.StatusCode);
                        var text = await response.Content.ReadAsStringAsync();
                        Assert.DoesNotContain(CollectorKey, text);
                        Assert.DoesNotContain(DashboardKey, text);
                        using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None }) return JObject.Load(reader);
                    }
                }
            }
            public void Dispose() { client.Dispose(); configuration.Dispose(); Database.Dispose(); logger.Dispose(); }
        }
        public void Dispose()
        {
            try { if (Directory.Exists(directory)) AssertLogsContainNoSecrets(); }
            finally
            {
                connection?.Dispose();
                try { admin.Execute("ALTER DATABASE [" + databaseName + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE"); admin.Execute("DROP DATABASE [" + databaseName + "]"); }
                finally { admin.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            }
        }
    }
}
