using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
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
using Xunit.Abstractions;

namespace IntegrationTests
{
    public sealed class FaultRegisterEndpointTests : IDisposable
    {
        private const string CollectorKey = "fake-collector-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string DashboardKey = "fake-dashboard-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string Secret = "unique-fault-secret";
        private static readonly DateTime Now = new DateTime(2026, 11, 13, 1, 2, 3, 456, DateTimeKind.Utc).AddTicks(7000);
        private readonly string databaseName = "MiniMES_FaultApi_" + Guid.NewGuid().ToString("N");
        private readonly string directory = Path.Combine(Path.GetTempPath(), "fault-api-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> usedConnections = new List<string>();
        private readonly SqlConnection admin;
        private readonly SqlConnection connection;
        private readonly string connectionString;
        private readonly int alarmId;
        private readonly ITestOutputHelper output;

        public FaultRegisterEndpointTests(ITestOutputHelper output)
        {
            this.output = output;
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
                alarmId = NewAlarm();
            }
            catch { Dispose(); throw; }
        }
        private int NewAlarm() { return connection.QuerySingle<int>(@"INSERT INTO dbo.ALARM (EquipmentId, Cause, RaisedAtUtc, ClearedAtUtc) OUTPUT INSERTED.AlarmId VALUES (1, 1, @now, @cleared)", new { now = Now, cleared = Now.AddMinutes(1) }); }
        private static string PathFor(string id) { return "/api/v1/alarms/" + id + "/fault"; }
        private static string Body(int category = 1, string cause = null, string action = null) { return JsonConvert.SerializeObject(new { causeCategory = category, causeText = cause, actionText = action }); }
        private Harness CreateHarness(string selected = null)
        {
            selected = selected ?? connectionString;
            usedConnections.Add(selected);
            return new Harness(selected, Path.Combine(directory, Guid.NewGuid().ToString("N") + ".log"));
        }
        private int Count() { return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.FAULT"); }
        private sealed class Row
        {
            public int FaultId { get; set; }
            public byte Status { get; set; }
            public byte CauseCategory { get; set; }
            public string CauseText { get; set; }
            public string ActionText { get; set; }
            public DateTime RegisteredAtUtc { get; set; }
            public byte[] Version { get; set; }
        }
        private Row Stored(int id) { return connection.QuerySingle<Row>("SELECT FaultId, Status, CauseCategory, CauseText, ActionText, RegisteredAtUtc, Version FROM dbo.FAULT WHERE AlarmId=@id AND Status <> 4", new { id }); }
        private static async Task<JObject> Send(Harness h, int id, string body, int status) { return await h.Send(HttpMethod.Post, PathFor(id.ToString()), DashboardKey, body, status); }
        private static void Error(JObject body, string error) { Assert.Equal(error, (string)body["error"]); }

        [Fact]
        public async Task RegisterFault_ValidRequest_StoresValuesAndReturnsCreated()
        {
            string path;
            using (var h = CreateHarness())
            {
                path = h.LogPath;
                var result = await Send(h, alarmId, Body(1, Secret + " cause", Secret + " action"), 201);
                Assert.Equal("registered", (string)result["status"]);
                Assert.True((int)result["faultId"] > 0);
                Assert.Equal("2026-11-13T01:02:03.456Z", (string)result["serverTimeUtc"]);
                var version = Convert.FromBase64String((string)result["version"]);
                Assert.Equal(8, version.Length);
                var row = Stored(alarmId);
                Assert.Equal(1, Count());
                Assert.Equal((byte)1, row.Status);
                Assert.Equal((byte)1, row.CauseCategory);
                Assert.Equal(Secret + " cause", row.CauseText);
                Assert.Equal(Secret + " action", row.ActionText);
                Assert.Equal(new DateTime(2026, 11, 13, 1, 2, 3, 456), row.RegisteredAtUtc);
                Assert.Equal(version, row.Version);
                Assert.Equal((int)result["faultId"], row.FaultId);
            }
            var log = Assert.Single(Logs(path));
            Assert.Null(log["@l"]);
            Assert.Equal("/api/v1/alarms/{alarmId}/fault", (string)log["Endpoint"]);
        }

        [Fact]
        public async Task RegisterFault_OptionalTexts_PreservesNullAndEmpty()
        {
            using (var h = CreateHarness())
            {
                foreach (var body in new[] { "{\"causeCategory\":1}", Body(), Body(1, "", "") })
                {
                    var id = NewAlarm();
                    await Send(h, id, body, 201);
                    var row = Stored(id);
                    Assert.Equal(body.Contains("\"\"" ) ? "" : null, row.CauseText);
                    Assert.Equal(body.Contains("\"\"" ) ? "" : null, row.ActionText);
                }
            }
        }

        [Fact]
        public async Task RegisterFault_RepeatedAndCancelled_ReturnsExistingOrCreatesNew()
        {
            using (var h = CreateHarness())
            {
                var first = await Send(h, alarmId, Body(), 201);
                var second = await Send(h, alarmId, Body(), 409);
                Error(second, "already_registered");
                Assert.Equal((int)first["faultId"], (int)second["faultId"]);
                Assert.Equal(1, Count());
                connection.Execute("UPDATE dbo.FAULT SET Status=4 WHERE AlarmId=@alarmId", new { alarmId });
                await Send(h, alarmId, Body(), 201);
                Assert.Equal(2, Count());
            }
        }

        [Fact]
        public async Task RegisterFault_MissingAlarmOrUnknownCategory_UsesRepositoryOrder()
        {
            using (var h = CreateHarness())
            {
                Error(await Send(h, alarmId + 1000, Body(), 404), "not_found");
                foreach (var category in new[] { 0, 7 }) Error(await Send(h, alarmId, Body(category), 400), "invalid_cause_category");
                Assert.Equal(0, Count());
                Error(await Send(h, alarmId + 1000, Body(7), 404), "not_found");
                await Send(h, alarmId, Body(), 201);
                Error(await Send(h, alarmId, Body(7), 400), "invalid_cause_category");
            }
        }

        [Fact]
        public async Task RegisterFault_InvalidCategoryOrTextTypes_ReturnsInvalidField()
        {
            using (var h = CreateHarness())
            {
                foreach (var value in new[] { null, "null", "\"1\"", "1.5", "true", "300", "-1", "2147483648" })
                    Error(await Send(h, alarmId, value == null ? "{}" : "{\"causeCategory\":" + value + "}", 400), "invalid_field");
                foreach (var field in new[] { "causeText", "actionText" })
                    foreach (var value in new[] { "5", "true", "{}", "[]" })
                        Error(await Send(h, alarmId, "{\"causeCategory\":1,\"" + field + "\":" + value + "}", 400), "invalid_field");
                Assert.Equal(0, Count());
            }
        }

        [Fact]
        public async Task RegisterFault_TextLengthAndMissingAlarm_UsesUtf16ValidationFirst()
        {
            using (var h = CreateHarness())
            {
                foreach (var field in new[] { "causeText", "actionText" })
                {
                    var valid = new JObject { ["causeCategory"] = 1, [field] = new string('x', 2000) };
                    await Send(h, NewAlarm(), valid.ToString(Formatting.None), 201);
                    valid[field] = new string('x', 2001);
                    Error(await Send(h, alarmId, valid.ToString(Formatting.None), 400), "invalid_field");
                    Error(await Send(h, alarmId + 1000, valid.ToString(Formatting.None), 400), "invalid_field");
                }
                var pairs = string.Concat(Enumerable.Repeat("😀", 1000));
                await Send(h, NewAlarm(), Body(1, pairs), 201);
                Error(await Send(h, alarmId, Body(1, pairs + "x"), 400), "invalid_field");
                Error(await Send(h, alarmId + 1000, Body(300), 400), "invalid_field");
            }
        }

        [Fact]
        public async Task RegisterFault_InvalidPathAndJson_ReturnsValidationErrors()
        {
            using (var h = CreateHarness())
            {
                foreach (var id in new[] { "abc", "0", "-1", "+1", "2147483648" })
                    Error(await h.Send(HttpMethod.Post, PathFor(id), DashboardKey, Body(), 400), "invalid_field");
                foreach (var body in new[] { "{", "", "{\"causeCategory\":1} {}" }) Error(await Send(h, alarmId, body, 400), "invalid_json");
                foreach (var body in new[] { "[]", "\"x\"" }) Error(await Send(h, alarmId, body, 400), "invalid_field");
            }
        }

        [Fact]
        public async Task RegisterFault_ContentLengthBoundary_RejectsExcessAndAcceptsLimit()
        {
            using (var h = CreateHarness())
            {
                Error(await Send(h, alarmId, Body().PadRight(65537), 400), "invalid_field");
                Assert.Equal(0, Count());
                await Send(h, alarmId, Body().PadRight(65536), 201);
            }
        }

        [Fact]
        public async Task RegisterFault_UnreachableDatabase_MarksDisconnected()
        {
            var builder = new SqlConnectionStringBuilder(connectionString) { DataSource = "tcp:127.0.0.1,1", ConnectTimeout = 2 };
            string path;
            using (var h = CreateHarness(builder.ConnectionString))
            {
                path = h.LogPath;
                Error(await Send(h, alarmId, Body(), 503), "database_unavailable");
                Assert.Equal("disconnected", h.Database.State);
            }
            Assert.Equal("Error", (string)Assert.Single(Logs(path))["@l"]);
        }

        [Fact]
        public async Task RegisterFault_KeyMatrix_EnforcesAuthorizationAndHidesSecrets()
        {
            string path;
            using (var h = CreateHarness())
            {
                path = h.LogPath;
                foreach (var key in new[] { null, "wrong-key" }) Error(await h.Send(HttpMethod.Post, PathFor(alarmId.ToString()), key, Body(), 401), "unauthorized");
                Error(await h.Send(HttpMethod.Post, PathFor(alarmId.ToString()), CollectorKey, Body(), 403), "forbidden");
                Assert.Equal(0, Count());
                await Send(h, alarmId, Body(), 201);
            }
            Assert.Equal(4, Logs(path).Length);
            AssertLogsContainNoSecrets();
        }

        // Simultaneous starts do not prove database overlap or exercise of the 2601/2627 path.
        [Fact]
        public void RegisterFault_ConcurrentRequests_StoresExactlyOneWinner()
        {
            var wins = 0;
            using (var h = CreateHarness())
            {
                for (var iteration = 0; iteration < 100; iteration++)
                {
                    var id = NewAlarm();
                    using (var barrier = new Barrier(2))
                    {
                        var tasks = Enumerable.Range(0, 2).Select(i => Task.Factory.StartNew(() =>
                        {
                            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                            return h.SendAny(PathFor(id.ToString()), Body(i + 1, Secret + " cause " + i, Secret + " action " + i));
                        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                        Assert.True(Task.WaitAll(tasks, TimeSpan.FromSeconds(30)));
                        var results = tasks.Select(t => t.Result).ToArray();
                        Assert.Single(results, x => x.Item1 == 201);
                        Assert.Single(results, x => x.Item1 == 409);
                        var winner = Array.FindIndex(results, x => x.Item1 == 201);
                        if (winner == 0) wins++;
                        Error(results[1 - winner].Item2, "already_registered");
                        Assert.Equal((int)results[winner].Item2["faultId"], (int)results[1 - winner].Item2["faultId"]);
                        var row = Stored(id);
                        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.FAULT WHERE AlarmId=@id AND Status <> 4", new { id }));
                        Assert.Equal((int)results[winner].Item2["faultId"], row.FaultId);
                        Assert.Equal((byte)(winner + 1), row.CauseCategory);
                        Assert.Equal(Secret + " cause " + winner, row.CauseText);
                        Assert.Equal(Secret + " action " + winner, row.ActionText);
                    }
                }
            }
            output.WriteLine("201: 100, 409: 100; request A wins: {0}; total pairs: 100", wins);
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
                Assert.DoesNotContain("unique-fault-secret", text);
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
            public Tuple<int, JObject> SendAny(string path, string body)
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, path))
                {
                    request.Headers.Add("X-Api-Key", DashboardKey);
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var response = client.SendAsync(request).GetAwaiter().GetResult())
                    {
                        var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        Assert.DoesNotContain(CollectorKey, text);
                        Assert.DoesNotContain(DashboardKey, text);
                        using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                            return Tuple.Create((int)response.StatusCode, JObject.Load(reader));
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
