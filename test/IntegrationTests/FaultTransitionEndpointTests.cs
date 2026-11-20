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
    public sealed class FaultTransitionEndpointTests : IDisposable
    {
        private const string CollectorKey = "fake-collector-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string DashboardKey = "fake-dashboard-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string Secret = "unique-transition-reason-secret";
        private static readonly DateTime Now = new DateTime(2026, 11, 13, 1, 2, 3, 456, DateTimeKind.Utc).AddTicks(7000);
        private static readonly DateTime Millis = Now.AddTicks(-7000);
        private static readonly DateTime Raised = Millis.AddHours(-2);
        private static readonly string[] Names = { null, "registered", "in_progress", "closed", "cancelled" };
        private readonly string databaseName = "MiniMES_Transition_" + Guid.NewGuid().ToString("N");
        private readonly string directory = Path.Combine(Path.GetTempPath(), "transition-api-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> connections = new List<string>();
        private readonly SqlConnection admin, connection;
        private readonly string connectionString;
        private readonly int userId;

        public FaultTransitionEndpointTests()
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
                userId = connection.QuerySingle<int>("INSERT INTO dbo.APP_USER (Name) OUTPUT INSERTED.UserId VALUES (N'Test User')");
            }
            catch { Dispose(); throw; }
        }
        private int NewAlarm() { return connection.QuerySingle<int>("INSERT INTO dbo.ALARM (EquipmentId, Cause, RaisedAtUtc, ClearedAtUtc) OUTPUT INSERTED.AlarmId VALUES (1, 1, @raised, @raised)", RaisedParameter()); }
        // Dapper sends DateTime as datetime (3.33 ms steps), which would shift the seeded millisecond value.
        private static DynamicParameters RaisedParameter(int? alarmId = null, int? status = null)
        {
            var parameters = new DynamicParameters();
            parameters.Add("raised", Raised, DbType.DateTime2);
            if (alarmId.HasValue) parameters.Add("alarmId", alarmId.Value);
            if (status.HasValue) parameters.Add("status", status.Value);
            return parameters;
        }
        private int NewFault(int status, int? alarm = null)
        {
            var alarmId = alarm ?? NewAlarm();
            return connection.QuerySingle<int>("INSERT INTO dbo.FAULT (AlarmId, Status, RegisteredAtUtc, CauseCategory) OUTPUT INSERTED.FaultId VALUES (@alarmId, @status, @raised, 1)", RaisedParameter(alarmId, status));
        }
        private static string PathFor(string id) { return "/api/v1/faults/" + id + "/transitions"; }
        private JObject Body(int from = 1, int to = 2, string reason = null, DateTime? recovered = null)
        {
            var body = new JObject { ["userId"] = userId, ["expectedStatus"] = Names[from], ["newStatus"] = Names[to] };
            if (reason != null) body["reason"] = reason;
            if (recovered.HasValue) body["recoveredAtUtc"] = recovered.Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            return body;
        }
        private Harness CreateHarness(string selected = null)
        {
            selected = selected ?? connectionString;
            connections.Add(selected);
            return new Harness(selected, Path.Combine(directory, Guid.NewGuid().ToString("N") + ".log"));
        }
        private static Task<JObject> Send(Harness h, int id, JObject body, int status) { return h.Send(PathFor(id.ToString()), DashboardKey, body.ToString(Formatting.None), status); }
        private static void Error(JObject body, string error) { Assert.Equal(error, (string)body["error"]); }
        private sealed class Row { public byte Status { get; set; } public DateTime? RecoveredAtUtc { get; set; } public DateTime? ClosedAtUtc { get; set; } public byte[] Version { get; set; } }
        private sealed class History { public int ChangedByUserId { get; set; } public byte PreviousStatus { get; set; } public byte NewStatus { get; set; } public string Reason { get; set; } public DateTime ChangedAtUtc { get; set; } }
        private Row Stored(int id) { return connection.QuerySingle<Row>("SELECT Status, RecoveredAtUtc, ClosedAtUtc, Version FROM dbo.FAULT WHERE FaultId=@id", new { id }); }
        private History[] Histories(int id) { return connection.Query<History>("SELECT ChangedByUserId, PreviousStatus, NewStatus, Reason, ChangedAtUtc FROM dbo.FAULT_HISTORY WHERE FaultId=@id ORDER BY FaultHistoryId", new { id }).ToArray(); }
        private void Unchanged(int id, int status) { Assert.Equal((byte)status, Stored(id).Status); Assert.Empty(Histories(id)); }

        [Fact]
        public async Task Transition_AllowedPairs_StoresHistoryAndReturnsVersion()
        {
            string path;
            using (var h = CreateHarness())
            {
                path = h.LogPath;
                foreach (var pair in new[] { new[] { 1, 2 }, new[] { 2, 3 }, new[] { 1, 4 }, new[] { 2, 4 }, new[] { 3, 2 }, new[] { 4, 1 } })
                {
                    var id = NewFault(pair[0]);
                    var result = await Send(h, id, Body(pair[0], pair[1], Secret, pair[1] == 3 ? (DateTime?)Raised : null), 200);
                    Assert.Equal(Names[pair[1]], (string)result["status"]);
                    Assert.Equal("2026-11-13T01:02:03.456Z", (string)result["serverTimeUtc"]);
                    var version = Convert.FromBase64String((string)result["version"]);
                    Assert.Equal(8, version.Length);
                    var row = Stored(id);
                    Assert.Equal((byte)pair[1], row.Status); Assert.Equal(version, row.Version);
                    if (pair[1] == 3) { Assert.Equal(Raised, row.RecoveredAtUtc); Assert.Equal(Millis, row.ClosedAtUtc); }
                    var history = Assert.Single(Histories(id));
                    Assert.Equal(userId, history.ChangedByUserId); Assert.Equal((byte)pair[0], history.PreviousStatus);
                    Assert.Equal((byte)pair[1], history.NewStatus); Assert.Equal(Secret, history.Reason); Assert.Equal(Millis, history.ChangedAtUtc);
                }
            }
            Assert.Equal(6, Logs(path).Length); Assert.All(Logs(path), log => Assert.Null(log["@l"]));
        }

        [Fact]
        public async Task Transition_RequiredReasonsMissing_ReturnsReasonRequired()
        {
            using (var h = CreateHarness())
                foreach (var pair in new[] { new[] { 1, 4 }, new[] { 2, 4 }, new[] { 3, 2 }, new[] { 4, 1 } })
                    foreach (var reason in new[] { (string)null, "null", "\"\"", "\"   \"" })
                    {
                        var id = NewFault(pair[0]); var body = Body(pair[0], pair[1]);
                        if (reason != null) body["reason"] = JToken.Parse(reason);
                        Error(await Send(h, id, body, 400), "reason_required"); Unchanged(id, pair[0]);
                    }
        }

        [Fact]
        public async Task Transition_OptionalReason_PreservesWhitespace()
        {
            using (var h = CreateHarness())
                foreach (var pair in new[] { new[] { 1, 2 }, new[] { 2, 3 } })
                {
                    var id = NewFault(pair[0]); var reason = "  " + Secret + "  ";
                    await Send(h, id, Body(pair[0], pair[1], reason, pair[1] == 3 ? (DateTime?)Raised : null), 200);
                    Assert.Equal(reason, Assert.Single(Histories(id)).Reason);
                }
        }

        [Fact]
        public async Task Transition_DisallowedPairs_ReturnsInvalidTransition()
        {
            using (var h = CreateHarness())
                for (var from = 1; from <= 4; from++) for (var to = 1; to <= 4; to++)
                {
                    if ((from == 1 && (to == 2 || to == 4)) || (from == 2 && (to == 3 || to == 4)) || (from == 3 && to == 2) || (from == 4 && to == 1)) continue;
                    var id = NewFault(from); Error(await Send(h, id, Body(from, to, Secret), 400), "invalid_transition"); Unchanged(id, from);
                }
        }

        [Fact]
        public async Task Transition_StaleStatus_PrecedesCoreValidation()
        {
            using (var h = CreateHarness())
            {
                var id = NewFault(2);
                foreach (var body in new[] { Body(1, 2), Body(1, 1), Body(1, 4), Body(1, 3) })
                {
                    var result = await Send(h, id, body, 409); Error(result, "status_mismatch"); Assert.Equal("in_progress", (string)result["currentStatus"]);
                }
                Unchanged(id, 2);
            }
        }

        [Fact]
        public async Task Transition_RecoveryBounds_AcceptsInclusiveEndpoints()
        {
            using (var h = CreateHarness())
            {
                foreach (var recovered in new DateTime?[] { null, Raised.AddMilliseconds(-1), Millis.AddMilliseconds(1) })
                {
                    var id = NewFault(2); Error(await Send(h, id, Body(2, 3, recovered: recovered), 400), "invalid_recovery_time"); Unchanged(id, 2);
                }
                var nullId = NewFault(2); var nullBody = Body(2, 3); nullBody["recoveredAtUtc"] = JValue.CreateNull();
                Error(await Send(h, nullId, nullBody, 400), "invalid_recovery_time"); Unchanged(nullId, 2);
                foreach (var recovered in new[] { Raised, Millis }) await Send(h, NewFault(2), Body(2, 3, recovered: recovered), 200);
            }
        }

        [Fact]
        public async Task Transition_RecoveryOnNonClosingTransition_UsesCoreOrder()
        {
            using (var h = CreateHarness())
            {
                foreach (var to in new[] { 2, 4 })
                {
                    var id = NewFault(1); Error(await Send(h, id, Body(1, to, Secret, Raised), 400), "invalid_recovery_time"); Unchanged(id, 1);
                }
                var other = NewFault(1); Error(await Send(h, other, Body(1, 4, recovered: Raised), 400), "reason_required"); Unchanged(other, 1);
            }
        }

        [Fact]
        public async Task Transition_RecoveryFormatErrors_ReturnsInvalidField()
        {
            using (var h = CreateHarness())
            {
                var id = NewFault(2);
                foreach (var value in new[] { "\"2026-11-13T01:02:03Z\"", "\"2026-11-13T01:02:03.4567Z\"", "\"2026-11-13T01:02:03.456+09:00\"", "\"\"", "\" \"", "5", "true" })
                { var body = Body(2, 3); body["recoveredAtUtc"] = JToken.Parse(value); Error(await Send(h, id, body, 400), "invalid_field"); }
                Unchanged(id, 2);
            }
        }

        [Fact]
        public async Task Transition_InvalidStatusTokens_ReturnsInvalidField()
        {
            using (var h = CreateHarness())
            {
                var id = NewFault(1);
                foreach (var field in new[] { "expectedStatus", "newStatus" }) foreach (var value in new[] { null, "null", "1", "\"Registered\"", "\" registered\"", "\"inProgress\"", "\"closed \"" })
                { var body = Body(); if (value == null) body.Remove(field); else body[field] = JToken.Parse(value); Error(await Send(h, id, body, 400), "invalid_field"); }
                Unchanged(id, 1);
            }
        }

        [Fact]
        public async Task Transition_UserValues_UsesRequiredTypeAndForeignKeyRules()
        {
            using (var h = CreateHarness())
            {
                var id = NewFault(1);
                foreach (var value in new[] { null, "null", "0", "-1", "\"1\"", "1.5", "true", "999999" })
                { var body = Body(); if (value == null) body.Remove("userId"); else body["userId"] = JToken.Parse(value); Error(await Send(h, id, body, 400), value == null || value == "null" || value == "0" || value == "-1" ? "user_required" : "invalid_field"); Unchanged(id, 1); }
            }
        }

        [Fact]
        public async Task Transition_MissingFaultAndInvalidFields_UsesValidationOrder()
        {
            using (var h = CreateHarness())
            {
                var missing = 999999; Error(await Send(h, missing, Body(), 404), "not_found");
                var body = Body(); body.Remove("userId"); Error(await Send(h, missing, body, 400), "user_required");
                body = Body(); body["newStatus"] = "bad"; Error(await Send(h, missing, body, 400), "invalid_field");
                body = Body(1, 4, new string('x', 1001)); Error(await Send(h, missing, body, 400), "invalid_field");
                body = Body(); body.Remove("userId"); body["newStatus"] = "bad"; Error(await Send(h, missing, body, 400), "invalid_field");
            }
        }

        [Fact]
        public async Task Transition_RestoreConflict_ReturnsActiveFaultThenRestores()
        {
            using (var h = CreateHarness())
            {
                var alarm = NewAlarm(); var id = NewFault(4, alarm); var other = NewFault(1, alarm);
                var result = await Send(h, id, Body(4, 1, Secret), 409); Error(result, "already_registered"); Assert.Equal(other, (int)result["faultId"]); Unchanged(id, 4); Unchanged(other, 1);
                connection.Execute("UPDATE dbo.FAULT SET Status=4 WHERE FaultId=@other", new { other });
                await Send(h, id, Body(4, 1, Secret), 200);
            }
        }

        [Fact]
        public async Task Transition_FullLifecycle_UpdatesTimesHistoryAndVersions()
        {
            using (var h = CreateHarness())
            {
                var id = NewFault(1); var versions = new List<string>();
                foreach (var body in new[] { Body(1, 2), Body(2, 3, recovered: Raised), Body(3, 2, Secret), Body(2, 3, recovered: Millis) })
                {
                    var result = await Send(h, id, body, 200); versions.Add((string)result["version"]); var row = Stored(id);
                    if ((string)body["newStatus"] == "closed") { Assert.Equal((string)body["recoveredAtUtc"], row.RecoveredAtUtc.Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")); Assert.Equal(Millis, row.ClosedAtUtc); }
                    else { Assert.Null(row.RecoveredAtUtc); Assert.Null(row.ClosedAtUtc); }
                }
                Assert.Equal(4, versions.Distinct().Count()); var history = Histories(id); Assert.Equal(4, history.Length);
                Assert.Equal(new byte[] { 1, 2, 3, 2 }, history.Select(x => x.PreviousStatus).ToArray()); Assert.Equal(new byte[] { 2, 3, 2, 3 }, history.Select(x => x.NewStatus).ToArray());
            }
        }

        [Fact]
        public async Task Transition_ReasonLengthAndTypes_UsesUtf16Units()
        {
            using (var h = CreateHarness())
            {
                await Send(h, NewFault(1), Body(1, 4, new string('x', 1000)), 200);
                var id = NewFault(1); Error(await Send(h, id, Body(1, 4, new string('x', 1001)), 400), "invalid_field");
                var pairs = string.Concat(Enumerable.Repeat("😀", 500)); await Send(h, NewFault(1), Body(1, 4, pairs), 200);
                Error(await Send(h, id, Body(1, 4, pairs + "x"), 400), "invalid_field");
                foreach (var value in new[] { "5", "true", "{}", "[]" }) { var body = Body(); body["reason"] = JToken.Parse(value); Error(await Send(h, id, body, 400), "invalid_field"); }
                Unchanged(id, 1);
            }
        }

        [Fact]
        public async Task Transition_InvalidPath_ReturnsInvalidField()
        {
            using (var h = CreateHarness()) foreach (var id in new[] { "abc", "0", "-1", "+1", "2147483648" }) Error(await h.Send(PathFor(id), DashboardKey, Body().ToString(Formatting.None), 400), "invalid_field");
        }

        [Fact]
        public async Task Transition_InvalidJsonAndNonObjects_ReturnsExpectedError()
        {
            using (var h = CreateHarness())
            {
                var id = NewFault(1);
                foreach (var body in new[] { "{", "", "{\"userId\":1} {}" }) Error(await h.Send(PathFor(id.ToString()), DashboardKey, body, 400), "invalid_json");
                foreach (var body in new[] { "[]", "\"x\"" }) Error(await h.Send(PathFor(id.ToString()), DashboardKey, body, 400), "invalid_field"); Unchanged(id, 1);
            }
        }

        [Fact]
        public async Task Transition_ContentLengthBoundary_RejectsExcessAndAcceptsLimit()
        {
            using (var h = CreateHarness())
            {
                var id = NewFault(1); var body = Body().ToString(Formatting.None);
                Error(await h.Send(PathFor(id.ToString()), DashboardKey, body.PadRight(65537), 400), "invalid_field"); Unchanged(id, 1);
                await h.Send(PathFor(id.ToString()), DashboardKey, body.PadRight(65536), 200);
            }
        }

        [Fact]
        public async Task Transition_UnreachableDatabase_MarksDisconnected()
        {
            var builder = new SqlConnectionStringBuilder(connectionString) { DataSource = "tcp:127.0.0.1,1", ConnectTimeout = 2 }; string path;
            using (var h = CreateHarness(builder.ConnectionString)) { path = h.LogPath; Error(await Send(h, 1, Body(), 503), "database_unavailable"); Assert.Equal("disconnected", h.Database.State); }
            Assert.Equal("Error", (string)Assert.Single(Logs(path))["@l"]);
        }

        [Fact]
        public async Task Transition_KeyMatrix_EnforcesAuthorizationAndHidesSecrets()
        {
            string path;
            using (var h = CreateHarness())
            {
                path = h.LogPath; var id = NewFault(1); var body = Body(1, 2, Secret).ToString(Formatting.None);
                foreach (var key in new[] { null, "wrong-key" }) Error(await h.Send(PathFor(id.ToString()), key, body, 401), "unauthorized");
                Error(await h.Send(PathFor(id.ToString()), CollectorKey, body, 403), "forbidden"); Unchanged(id, 1);
                await h.Send(PathFor(id.ToString()), DashboardKey, body, 200);
            }
            Assert.Equal(4, Logs(path).Length); AssertLogsContainNoSecrets();
        }

        private static JObject[] Logs(string path) { return File.ReadAllLines(path).Where(x => !string.IsNullOrWhiteSpace(x)).Select(JObject.Parse).ToArray(); }
        private void AssertLogsContainNoSecrets()
        {
            foreach (var path in Directory.GetFiles(directory, "*.log"))
            {
                var text = File.ReadAllText(path); Assert.DoesNotContain(CollectorKey, text); Assert.DoesNotContain(DashboardKey, text);
                foreach (var value in connections) Assert.DoesNotContain(value, text); Assert.DoesNotContain(Secret, text);
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
                LogPath = logPath; ServerSettings settings;
                Assert.Empty(ServerSettings.Validate(new Dictionary<string, string> { ["SERVER_URL"] = "https://+:5443/", ["DB_CONNECTION_STRING"] = connectionString, ["COLLECTOR_API_KEY"] = CollectorKey, ["DASHBOARD_API_KEY"] = DashboardKey }, out settings));
                logger = new LoggerConfiguration().WriteTo.File(new CompactJsonFormatter(), logPath).CreateLogger();
                Database = new DatabaseMonitor(() => false, TimeSpan.FromSeconds(2));
                configuration = ApiConfiguration.Create(new ApiRuntime(settings, new CollectorState(() => TimeSpan.Zero, TimeSpan.FromSeconds(10)), Database, () => Now, logger));
                client = new HttpClient(new HttpServer(configuration)) { BaseAddress = new Uri("http://localhost/") };
            }
            public async Task<JObject> Send(string path, string key, string body, int status)
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, path))
                {
                    if (key != null) request.Headers.Add("X-Api-Key", key);
                    if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var response = await client.SendAsync(request))
                    {
                        var text = await response.Content.ReadAsStringAsync(); Assert.True(status == (int)response.StatusCode, "Expected " + status + " but got " + (int)response.StatusCode + ": " + text);
                        Assert.DoesNotContain(CollectorKey, text); Assert.DoesNotContain(DashboardKey, text);
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
