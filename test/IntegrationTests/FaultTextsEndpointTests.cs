using System;
using System.Collections.Generic;
using System.Data;
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
    public sealed class FaultTextsEndpointTests : IDisposable
    {
        private const string CollectorKey = "fake-collector-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string DashboardKey = "fake-dashboard-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string Secret = "unique-texts-secret";
        private static readonly DateTime Now = new DateTime(2026, 11, 13, 1, 2, 3, 456, DateTimeKind.Utc).AddTicks(7000);
        private readonly string databaseName = "MiniMES_TextApi_" + Guid.NewGuid().ToString("N");
        private readonly string directory = Path.Combine(Path.GetTempPath(), "texts-api-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> connections = new List<string>();
        private readonly SqlConnection admin;
        private readonly SqlConnection connection;
        private readonly string connectionString;
        private readonly int faultId;

        public FaultTextsEndpointTests()
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
                faultId = NewFault(1);
            }
            catch { Dispose(); throw; }
        }

        private int NewFault(byte status)
        {
            var p = new DynamicParameters();
            p.Add("now", Now, DbType.DateTime2);
            p.Add("cleared", Now.AddMinutes(1), DbType.DateTime2);
            var alarm = connection.QuerySingle<int>("INSERT INTO dbo.ALARM (EquipmentId, Cause, RaisedAtUtc, ClearedAtUtc) OUTPUT inserted.AlarmId VALUES (1, 1, @now, @cleared)", p);
            p.Add("alarm", alarm);
            p.Add("status", status);
            return connection.QuerySingle<int>("INSERT INTO dbo.FAULT (AlarmId, Status, RegisteredAtUtc, CauseCategory) OUTPUT inserted.FaultId VALUES (@alarm, @status, @now, 1)", p);
        }
        private sealed class Row
        {
            public byte CauseCategory { get; set; }
            public string CauseText { get; set; }
            public string ActionText { get; set; }
            public byte[] Version { get; set; }
        }
        private Row Read(int id) { return connection.QuerySingle<Row>("SELECT CauseCategory, CauseText, ActionText, Version FROM dbo.FAULT WHERE FaultId=@id", new { id }); }
        private static string Version(Row row) { return Convert.ToBase64String(row.Version); }
        private static JObject Body(string version, int category = 1, string cause = null, string action = null)
        {
            return new JObject { ["expectedVersion"] = version, ["causeCategory"] = category, ["causeText"] = cause == null ? JValue.CreateNull() : new JValue(cause), ["actionText"] = action == null ? JValue.CreateNull() : new JValue(action) };
        }
        private Harness CreateHarness(string selected = null)
        {
            selected = selected ?? connectionString;
            connections.Add(selected);
            return new Harness(selected, Path.Combine(directory, Guid.NewGuid().ToString("N") + ".log"));
        }
        private static string PathFor(string id) { return "/api/v1/faults/" + id + "/texts"; }
        private static Task<JObject> Send(Harness h, int id, JObject body, int status) { return h.Send(PathFor(id.ToString()), DashboardKey, body.ToString(Formatting.None), status); }
        private static void Error(JObject body, string error) { Assert.Equal(error, (string)body["error"]); }
        private void Unchanged(int id, Row before)
        {
            var after = Read(id);
            Assert.Equal(before.Version, after.Version);
            Assert.Equal(before.CauseCategory, after.CauseCategory);
            Assert.Equal(before.CauseText, after.CauseText);
            Assert.Equal(before.ActionText, after.ActionText);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        public async Task UpdateTexts_OpenOrCancelled_StoresAndReturnsVersion(byte status)
        {
            var id = NewFault(status);
            var before = Read(id);
            string path;
            using (var h = CreateHarness())
            {
                path = h.LogPath;
                var result = await Send(h, id, Body(Version(before), 2, Secret + " cause", Secret + " action"), 200);
                Assert.Equal("2026-11-13T01:02:03.456Z", (string)result["serverTimeUtc"]);
                var after = Read(id);
                Assert.Equal((byte)2, after.CauseCategory);
                Assert.Equal(Secret + " cause", after.CauseText);
                Assert.Equal(Secret + " action", after.ActionText);
                Assert.Equal(8, Convert.FromBase64String((string)result["version"]).Length);
                Assert.Equal(after.Version, Convert.FromBase64String((string)result["version"]));
                Assert.NotEqual(Version(before), Version(after));
            }
            var log = Assert.Single(Logs(path));
            Assert.Null(log["@l"]);
            Assert.Equal("/api/v1/faults/{faultId}/texts", (string)log["Endpoint"]);
        }

        [Fact]
        public async Task UpdateTexts_ConsecutiveRequests_UsesReturnedVersion()
        {
            using (var h = CreateHarness())
            {
                var version = Version(Read(faultId));
                for (var i = 0; i < 3; i++)
                {
                    var result = await Send(h, faultId, Body(version, 1, Secret + i), 200);
                    var next = (string)result["version"];
                    Assert.NotEqual(version, next);
                    Assert.Equal(Version(Read(faultId)), next);
                    version = next;
                }
            }
        }

        [Fact]
        public async Task UpdateTexts_NullAndWhitespace_PreservesValues()
        {
            using (var h = CreateHarness())
            {
                foreach (var pair in new[] { Tuple.Create<string, string>(null, "a"), Tuple.Create<string, string>("b", null), Tuple.Create<string, string>(null, null), Tuple.Create("", "   ") })
                {
                    await Send(h, faultId, Body(Version(Read(faultId)), 1, pair.Item1, pair.Item2), 200);
                    var row = Read(faultId);
                    Assert.Equal(pair.Item1, row.CauseText);
                    Assert.Equal(pair.Item2, row.ActionText);
                }
            }
        }

        [Fact]
        public async Task UpdateTexts_StaleVersion_ReturnsCurrentValuesIncludingNull()
        {
            using (var h = CreateHarness())
            {
                foreach (var isNull in new[] { false, true })
                {
                    var stale = Version(Read(faultId));
                    await Send(h, faultId, Body(stale, 2, isNull ? null : Secret + " current cause", isNull ? null : Secret + " current action"), 200);
                    var current = Read(faultId);
                    var result = await Send(h, faultId, Body(stale, 1, Secret + " request cause", Secret + " request action"), 409);
                    Error(result, "version_conflict");
                    Assert.Equal((int)current.CauseCategory, (int)result["causeCategory"]);
                    Assert.NotNull(result.Property("causeText"));
                    Assert.NotNull(result.Property("actionText"));
                    Assert.Equal(current.CauseText, (string)result["causeText"]);
                    Assert.Equal(current.ActionText, (string)result["actionText"]);
                    Assert.Equal(Version(current), (string)result["version"]);
                    Unchanged(faultId, current);
                }
            }
        }

        [Fact]
        public async Task UpdateTexts_ClosedAndUnknownCategory_UsesRepositoryOrder()
        {
            var closed = NewFault(3);
            using (var h = CreateHarness())
            {
                var before = Read(closed);
                foreach (var version in new[] { Version(before), Convert.ToBase64String(new byte[8]) })
                {
                    var result = await Send(h, closed, Body(version), 409);
                    Error(result, "fault_closed");
                    Assert.Equal("closed", (string)result["status"]);
                    Unchanged(closed, before);
                }
                foreach (var id in new[] { faultId, closed })
                {
                    var row = Read(id);
                    foreach (var category in new[] { 0, 7 })
                        Error(await Send(h, id, Body(Version(row), category), 400), "invalid_cause_category");
                    Unchanged(id, row);
                }
            }
        }

        [Fact]
        public async Task UpdateTexts_InvalidFields_RejectsWithoutWriting()
        {
            using (var h = CreateHarness())
            {
                var before = Read(faultId);
                var valid = Body(Version(before));
                var cases = new List<JObject>();
                foreach (var raw in new[] { null, "null", "\"1\"", "1.5", "true", "300", "-1" })
                {
                    var b = (JObject)valid.DeepClone();
                    if (raw == null) b.Remove("causeCategory"); else b["causeCategory"] = JToken.Parse(raw);
                    cases.Add(b);
                }
                var canonical = Convert.ToBase64String(new byte[8]);
                foreach (var raw in new[] { null, "null", "5", "true", "\"\"", JsonConvert.SerializeObject(Convert.ToBase64String(new byte[7])), JsonConvert.SerializeObject(Convert.ToBase64String(new byte[9])), "\"!\"", JsonConvert.SerializeObject(" " + canonical), JsonConvert.SerializeObject(canonical + " "), JsonConvert.SerializeObject(canonical.Insert(4, "\n")), JsonConvert.SerializeObject(canonical.TrimEnd('=')), "\"AAAAAAAAAAB=\"" })
                {
                    var b = (JObject)valid.DeepClone();
                    if (raw == null) b.Remove("expectedVersion"); else b["expectedVersion"] = JToken.Parse(raw);
                    cases.Add(b);
                }
                foreach (var field in new[] { "causeText", "actionText" })
                    foreach (var raw in new[] { null, "5", "true", "{}", "[]" })
                    {
                        var b = (JObject)valid.DeepClone();
                        if (raw == null) b.Remove(field); else b[field] = JToken.Parse(raw);
                        cases.Add(b);
                    }
                foreach (var body in cases) Error(await Send(h, faultId, body, 400), "invalid_field");
                Unchanged(faultId, before);
            }
        }

        [Fact]
        public async Task UpdateTexts_TextLength_UsesUtf16Units()
        {
            using (var h = CreateHarness())
            {
                foreach (var field in new[] { "causeText", "actionText" })
                {
                    foreach (var value in new[] { new string('x', 2000), string.Concat(Enumerable.Repeat("😀", 1000)) })
                    {
                        var b = Body(Version(Read(faultId))); b[field] = value;
                        await Send(h, faultId, b, 200);
                        var before = Read(faultId);
                        b["expectedVersion"] = Version(before); b[field] = value + "x";
                        Error(await Send(h, faultId, b, 400), "invalid_field");
                        Unchanged(faultId, before);
                    }
                }
            }
        }

        [Fact]
        public async Task UpdateTexts_MissingFault_ValidatesFieldsBeforeLookup()
        {
            using (var h = CreateHarness())
            {
                var id = faultId + 100000;
                var b = Body(Version(Read(faultId)));
                Error(await Send(h, id, b, 404), "not_found");
                b["expectedVersion"] = "bad"; Error(await Send(h, id, b, 400), "invalid_field");
                b = Body(Version(Read(faultId)), 1, new string('x', 2001)); Error(await Send(h, id, b, 400), "invalid_field");
                b = Body(Version(Read(faultId)), 300); Error(await Send(h, id, b, 400), "invalid_field");
                b["causeCategory"] = 7; Error(await Send(h, id, b, 404), "not_found");
            }
        }

        [Fact]
        public async Task UpdateTexts_InvalidPathAndJson_ReturnsValidationErrors()
        {
            using (var h = CreateHarness())
            {
                var body = Body(Version(Read(faultId))).ToString(Formatting.None);
                foreach (var id in new[] { "abc", "0", "-1", "+1", "2147483648" })
                    Error(await h.Send(PathFor(id), DashboardKey, body, 400), "invalid_field");
                foreach (var invalid in new[] { "{", "", "{\"a\":1} {}" })
                    Error(await h.Send(PathFor(faultId.ToString()), DashboardKey, invalid, 400), "invalid_json");
                foreach (var invalid in new[] { "[]", "\"x\"" })
                    Error(await h.Send(PathFor(faultId.ToString()), DashboardKey, invalid, 400), "invalid_field");
            }
        }

        [Fact]
        public async Task UpdateTexts_ContentLengthAndExtraKeys_RespectsBoundary()
        {
            using (var h = CreateHarness())
            {
                var before = Read(faultId);
                var b = Body(Version(before)); b["userId"] = 17; b["extra"] = "ignored";
                var text = b.ToString(Formatting.None);
                Error(await h.Send(PathFor(faultId.ToString()), DashboardKey, text.PadRight(65537), 400), "invalid_field");
                Unchanged(faultId, before);
                await h.Send(PathFor(faultId.ToString()), DashboardKey, text.PadRight(65536), 200);
            }
        }

        [Fact]
        public async Task UpdateTexts_UnreachableDatabase_MarksDisconnected()
        {
            var builder = new SqlConnectionStringBuilder(connectionString) { DataSource = "tcp:127.0.0.1,1", ConnectTimeout = 2 };
            string path;
            using (var h = CreateHarness(builder.ConnectionString))
            {
                path = h.LogPath;
                Error(await Send(h, faultId, Body(Version(Read(faultId))), 503), "database_unavailable");
                Assert.Equal("disconnected", h.Database.State);
            }
            Assert.Equal("Error", (string)Assert.Single(Logs(path))["@l"]);
        }

        [Fact]
        public async Task UpdateTexts_KeyMatrix_EnforcesAuthorization()
        {
            string path;
            using (var h = CreateHarness())
            {
                path = h.LogPath;
                var before = Read(faultId);
                var body = Body(Version(before)).ToString(Formatting.None);
                foreach (var key in new[] { null, "wrong-key" }) Error(await h.Send(PathFor(faultId.ToString()), key, body, 401), "unauthorized");
                Error(await h.Send(PathFor(faultId.ToString()), CollectorKey, body, 403), "forbidden");
                Unchanged(faultId, before);
                await h.Send(PathFor(faultId.ToString()), DashboardKey, body, 200);
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
                foreach (var value in connections) Assert.DoesNotContain(value, text);
                Assert.DoesNotContain(Secret, text);
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
            public async Task<JObject> Send(string path, string key, string body, int status)
            {
                using (var request = new HttpRequestMessage(HttpMethod.Put, path))
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
