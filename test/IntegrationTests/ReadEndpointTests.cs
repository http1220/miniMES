using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using Core;
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
    public sealed class ReadEndpointTests : IDisposable
    {
        private const string CollectorKey = "fake-collector-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string DashboardKey = "fake-dashboard-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string WrongKey = "fake-wrong-cccccccccccccccccccccccccccccccc";
        private const string Equipment = "/api/v1/equipment/latest";
        private const string Alarms = "/api/v1/alarms";
        private const string Users = "/api/v1/users";
        private const string Faults = "/api/v1/faults";
        private const string ServerTime = "2026-11-13T01:02:03.456Z";
        private static readonly DateTime Now = new DateTime(2026, 11, 13, 1, 2, 3, 456, DateTimeKind.Utc);
        private readonly string databaseName = "MiniMES_Read_" + Guid.NewGuid().ToString("N");
        private readonly SqlConnection admin;
        private readonly SqlConnection connection;
        private readonly string connectionString;

        public ReadEndpointTests()
        {
            var configured = Environment.GetEnvironmentVariable("MINIMES_TEST_SQL_CONNECTION_STRING");
            Assert.False(string.IsNullOrWhiteSpace(configured),
                "Set MINIMES_TEST_SQL_CONNECTION_STRING to a test SQL Server connection string with CREATE DATABASE permission.");
            var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master", Pooling = false };
            admin = new SqlConnection(builder.ConnectionString);
            try
            {
                admin.Open();
                admin.Execute("CREATE DATABASE [" + databaseName + "]");
            }
            catch
            {
                admin.Dispose();
                throw;
            }

            builder.InitialCatalog = databaseName;
            connectionString = builder.ConnectionString;
            connection = new SqlConnection(connectionString);
            try
            {
                MigrationRunner.Run(connectionString);
                connection.Open();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        [Fact]
        public async Task KeyMatrix_AllReadEndpoints_EnforcesAccessAndHidesKeys()
        {
            var fault = InsertFault(InsertAlarm(1, 1, Now), FaultStatus.Registered);
            var logPath = Path.Combine(Path.GetTempPath(), "read-key-matrix-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                using (var logger = new LoggerConfiguration().WriteTo.File(new CompactJsonFormatter(), logPath).CreateLogger())
                using (var database = new DatabaseMonitor(() => false, TimeSpan.FromSeconds(2)))
                using (var client = CreateClient(database, logger))
                {
                    foreach (var endpoint in new[] { Equipment, Alarms + "?active=true", Users + "?active=true", Faults, FaultPath(fault) })
                    {
                        await AssertError(client, endpoint, 401, "unauthorized", null);
                        await AssertError(client, endpoint, 401, "unauthorized", WrongKey);
                        await AssertError(client, endpoint, 403, "forbidden", CollectorKey);
                        await Send(client, endpoint);
                    }
                    Assert.Equal("checking", database.State);
                }

                var logs = File.ReadAllText(logPath);
                Assert.Contains("Authentication", logs);
                AssertNoKeys(logs);
                Assert.DoesNotContain(connectionString, logs);
            }
            finally
            {
                if (File.Exists(logPath)) File.Delete(logPath);
            }
        }

        [Fact]
        public async Task LatestEquipment_ReturnsOrderedFieldsAndNormalizesInvalidStatus()
        {
            InsertAlarm(1, 1, Now);
            InsertAlarm(2, 1, Now, cleared: true);
            InsertAlarm(2, 2, Now);
            var sampleParameters = new DynamicParameters(new { runId = Guid.NewGuid() });
            sampleParameters.Add("now", Now, DbType.DateTime2);
            connection.Execute(@"
INSERT INTO dbo.COLLECTOR_RUN (RunId, StartedAtUtc) VALUES (@runId, @now);
INSERT INTO dbo.SAMPLE
    (RunId, SequenceNumber, EquipmentId, CollectedAtUtc, RecordedAtUtc, Temperature, Vibration, StatusCode)
VALUES (@runId, 1, 1, DATEADD(second, -1, @now), @now, 99, 9, 0),
       (@runId, 2, 1, @now, @now, 42.5, 1.25, 2),
       (@runId, 3, 2, @now, @now, 60, 2, 255),
       (@runId, 4, 4, @now, @now, 41, 1, 0),
       (@runId, 5, 5, @now, @now, 43, 1, 1)", sampleParameters);

            using (var database = new DatabaseMonitor(() => false, TimeSpan.FromSeconds(2)))
            using (var client = CreateClient(database))
            {
                var body = await Send(client, Equipment);
                Assert.Equal(ServerTime, (string)body["serverTimeUtc"]);
                var equipment = (JArray)body["equipment"];
                Assert.Equal(Enumerable.Range(1, 5), equipment.Select(row => (int)row["equipmentId"]));
                Assert.Equal("설비 1", (string)equipment[0]["name"]);
                Assert.Equal("lost", (string)equipment[0]["connection"]);
                Assert.All(equipment.Skip(1), row => Assert.Equal("connected", (string)row["connection"]));
                Assert.Equal(2, (int)equipment[0]["statusCode"]);
                Assert.Equal(42.5, (double)equipment[0]["temperature"]);
                Assert.Equal(1.25, (double)equipment[0]["vibration"]);
                Assert.Equal(ServerTime, (string)equipment[0]["lastSampleAtUtc"]);
                Assert.Equal(JTokenType.Null, equipment[1]["statusCode"].Type);
                Assert.Equal(ServerTime, (string)equipment[1]["lastSampleAtUtc"]);
                foreach (var field in new[] { "statusCode", "temperature", "vibration", "lastSampleAtUtc" })
                    Assert.Equal(JTokenType.Null, equipment[2][field].Type);
                Assert.Equal(0, (int)equipment[3]["statusCode"]);
                Assert.Equal(1, (int)equipment[4]["statusCode"]);
            }
        }

        [Fact]
        public async Task ActiveAlarms_ReturnsOrderedActiveRowsWithAcknowledgementAndNonCancelledFault()
        {
            var user = InsertUser("Operator", true);
            var oldest = InsertAlarm(1, 1, Now.AddSeconds(-1));
            var cancelledOnly = InsertAlarm(1, 2, Now);
            var acknowledged = InsertAlarm(1, 3, Now, acknowledgedBy: user);
            InsertAlarm(2, 1, Now.AddSeconds(1), cleared: true);
            InsertFault(cancelledOnly, FaultStatus.Cancelled);
            InsertFault(acknowledged, FaultStatus.Cancelled);
            var fault = InsertFault(acknowledged, FaultStatus.InProgress);

            using (var database = new DatabaseMonitor(() => false, TimeSpan.FromSeconds(2)))
            using (var client = CreateClient(database))
            {
                var body = await Send(client, Alarms + "?active=true");
                Assert.Equal(ServerTime, (string)body["serverTimeUtc"]);
                var alarms = (JArray)body["alarms"];
                Assert.Equal(new[] { acknowledged, cancelledOnly, oldest }, alarms.Select(row => (int)row["alarmId"]));
                Assert.Equal(1, (int)alarms[0]["equipmentId"]);
                Assert.Equal(3, (int)alarms[0]["cause"]);
                Assert.Equal(ServerTime, (string)alarms[0]["raisedAtUtc"]);
                Assert.Equal(ServerTime, (string)alarms[0]["acknowledgedAtUtc"]);
                Assert.Equal(user, (int)alarms[0]["acknowledgedByUserId"]);
                Assert.Equal("Operator", (string)alarms[0]["acknowledgedByName"]);
                Assert.Equal(fault, (int)alarms[0]["faultId"]);
                foreach (var row in alarms.Skip(1))
                    foreach (var field in new[] { "acknowledgedAtUtc", "acknowledgedByUserId", "acknowledgedByName", "faultId" })
                        Assert.Equal(JTokenType.Null, row[field].Type);
            }
        }

        [Fact]
        public async Task ActiveUsers_ExcludesInactiveAndOrdersByName()
        {
            var zoe = InsertUser("Zoe", true);
            InsertUser("Inactive", false);
            var ada = InsertUser("Ada", true);

            using (var database = new DatabaseMonitor(() => false, TimeSpan.FromSeconds(2)))
            using (var client = CreateClient(database))
            {
                var body = await Send(client, Users + "?active=true");
                Assert.Equal(ServerTime, (string)body["serverTimeUtc"]);
                var users = (JArray)body["users"];
                Assert.Equal(new[] { ada, zoe }, users.Select(row => (int)row["userId"]));
                Assert.Equal(new[] { "Ada", "Zoe" }, users.Select(row => (string)row["name"]));
            }
        }

        [Fact]
        public async Task ActiveQuery_RequiresExactlyOneTrueValue()
        {
            using (var database = new DatabaseMonitor(() => false, TimeSpan.FromSeconds(2)))
            using (var client = CreateClient(database))
            {
                foreach (var endpoint in new[] { Alarms, Users })
                {
                    await Send(client, endpoint + "?active=TrUe");
                    foreach (var query in new[] { "", "?active=", "?active=false", "?active=abc", "?active=%20true", "?active=true&active=false" })
                        await AssertError(client, endpoint + query, 400, "invalid_field");
                }
            }
        }

        [Fact]
        public async Task FaultList_FiltersAllStatusesAndMapsCompletionAndVersion()
        {
            var alarm = InsertAlarm(1, 1, Now);
            var registered = InsertFault(alarm, FaultStatus.Registered);
            var inProgress = InsertFault(InsertAlarm(2, 1, Now.AddSeconds(-1)), FaultStatus.InProgress);
            var closed = InsertFault(InsertAlarm(3, 1, Now), FaultStatus.Closed, Now.AddSeconds(1));
            var cancelled = InsertFault(alarm, FaultStatus.Cancelled);

            using (var database = new DatabaseMonitor(() => false, TimeSpan.FromSeconds(2)))
            using (var client = CreateClient(database))
            {
                var all = await Send(client, Faults);
                Assert.Equal(ServerTime, (string)all["serverTimeUtc"]);
                Assert.Equal(new[] { cancelled, closed, registered, inProgress },
                    ((JArray)all["faults"]).Select(row => (int)row["faultId"]));

                var expected = new Dictionary<string, int>
                {
                    ["registered"] = registered, ["in_progress"] = inProgress, ["closed"] = closed, ["cancelled"] = cancelled
                };
                foreach (var entry in expected)
                {
                    var body = await Send(client, Faults + "?status=" + entry.Key);
                    var row = Assert.Single((JArray)body["faults"]);
                    Assert.Equal(entry.Value, (int)row["faultId"]);
                    Assert.Equal(entry.Key, (string)row["status"]);
                    Assert.Equal(8, Convert.FromBase64String((string)row["version"]).Length);
                    if (entry.Key == "closed")
                        Assert.Equal("2026-11-13T01:02:04.456Z", (string)row["completedAtUtc"]);
                    else
                        Assert.Equal(JTokenType.Null, row["completedAtUtc"].Type);
                }

                var first = ((JArray)all["faults"])[2];
                Assert.Equal(alarm, (int)first["alarmId"]);
                Assert.Equal(1, (int)first["equipmentId"]);
                Assert.Equal(1, (int)first["causeCategory"]);
                Assert.Equal(ServerTime, (string)first["alarmRaisedAtUtc"]);
                Assert.Equal("2026-11-13T01:03:03.456Z", (string)first["registeredAtUtc"]);
                foreach (var query in new[] { "?status=", "?status=abc", "?status=Closed", "?status=1", "?status=closed&status=registered" })
                    await AssertError(client, Faults + query, 400, "invalid_field");
            }
        }

        [Fact]
        public async Task FaultList_ReturnsEmptyArrayAndLimitsAfterFiltering()
        {
            using (var database = new DatabaseMonitor(() => false, TimeSpan.FromSeconds(2)))
            using (var client = CreateClient(database))
            {
                Assert.Empty((JArray)(await Send(client, Faults))["faults"]);
                var older = InsertFault(InsertAlarm(2, 1, Now.AddSeconds(-1)), FaultStatus.Registered);
                var alarm = InsertAlarm(1, 1, Now);
                var ids = connection.Query<int>(@"
INSERT INTO dbo.FAULT (AlarmId, Status, RegisteredAtUtc, CauseCategory)
OUTPUT INSERTED.FaultId
SELECT TOP (201) @alarm, 4, @now, 1
FROM dbo.EQUIPMENT AS a CROSS JOIN dbo.EQUIPMENT AS b
CROSS JOIN dbo.EQUIPMENT AS c CROSS JOIN dbo.EQUIPMENT AS d",
                    new { alarm, now = Now }).ToArray();
                Assert.Equal(201, ids.Length);
                var body = await Send(client, Faults);
                Assert.Equal(ids.OrderByDescending(id => id).Take(200),
                    ((JArray)body["faults"]).Select(row => (int)row["faultId"]));
                Assert.Empty((JArray)(await Send(client, Faults + "?status=closed"))["faults"]);
                var registered = Assert.Single((JArray)(await Send(client, Faults + "?status=registered"))["faults"]);
                Assert.Equal(older, (int)registered["faultId"]);
            }
        }

        [Fact]
        public async Task FaultDetail_ReturnsListFieldsTextsAndOrderedHistory()
        {
            var operatorId = InsertUser("Operator", true);
            var supervisor = InsertUser("Supervisor", false);
            var alarm = InsertAlarm(1, 1, Now);
            var fault = InsertFault(alarm, FaultStatus.Cancelled);
            var withoutHistory = InsertFault(alarm, FaultStatus.Registered);
            InsertHistory(fault, supervisor, Now.AddSeconds(1), FaultStatus.InProgress, FaultStatus.Cancelled, "cancel reason");
            InsertHistory(fault, operatorId, Now, FaultStatus.Registered, FaultStatus.InProgress, null);

            using (var database = new DatabaseMonitor(() => false, TimeSpan.FromSeconds(2)))
            using (var client = CreateClient(database))
            {
                var detail = await Send(client, FaultPath(fault));
                Assert.Equal(ServerTime, (string)detail["serverTimeUtc"]);
                var listRow = Assert.Single((JArray)(await Send(client, Faults + "?status=cancelled"))["faults"]);
                foreach (var property in ((JObject)listRow).Properties())
                    Assert.True(JToken.DeepEquals(property.Value, detail[property.Name]), property.Name);
                Assert.Equal("cause text", (string)detail["causeText"]);
                Assert.Equal("action text", (string)detail["actionText"]);
                Assert.Equal("cancel reason", (string)detail["cancelReason"]);
                var history = (JArray)detail["history"];
                Assert.Equal(new[] { "Operator", "Supervisor" }, history.Select(row => (string)row["changedByName"]));
                Assert.Equal(new[] { ServerTime, "2026-11-13T01:02:04.456Z" }, history.Select(row => (string)row["changedAtUtc"]));
                Assert.Equal(new[] { "registered", "in_progress" }, history.Select(row => (string)row["previousStatus"]));
                Assert.Equal(new[] { "in_progress", "cancelled" }, history.Select(row => (string)row["newStatus"]));
                Assert.Equal(JTokenType.Null, history[0]["reason"].Type);
                Assert.Equal("cancel reason", (string)history[1]["reason"]);

                var empty = await Send(client, FaultPath(withoutHistory));
                Assert.Equal(JTokenType.Null, empty["cancelReason"].Type);
                Assert.Empty((JArray)empty["history"]);
            }
        }

        [Fact]
        public async Task FaultDetail_RejectsInvalidIdAndReturnsNotFoundForMissingFault()
        {
            using (var database = new DatabaseMonitor(() => false, TimeSpan.FromSeconds(2)))
            using (var client = CreateClient(database))
            {
                foreach (var id in new[] { "abc", "0", "-1", "+1", "2147483648" })
                    await AssertError(client, Faults + "/" + id, 400, "invalid_field");
                await AssertError(client, FaultPath(1), 404, "not_found");
            }
        }

        private HttpClient CreateClient(DatabaseMonitor database, ILogger logger = null)
        {
            ServerSettings settings;
            Assert.Empty(ServerSettings.Validate(new Dictionary<string, string>
            {
                ["SERVER_URL"] = "https://+:5443/",
                ["DB_CONNECTION_STRING"] = connectionString,
                ["COLLECTOR_API_KEY"] = CollectorKey,
                ["DASHBOARD_API_KEY"] = DashboardKey
            }, out settings));
            var runtime = new ApiRuntime(settings, new CollectorState(() => TimeSpan.Zero, TimeSpan.FromSeconds(10)),
                database, () => Now.AddTicks(7000), logger ?? Log.Logger);
            return new HttpClient(new HttpServer(ApiConfiguration.Create(runtime))) { BaseAddress = new Uri("http://localhost/") };
        }

        private static string FaultPath(int id)
        {
            return Faults + "/" + id.ToString(CultureInfo.InvariantCulture);
        }

        private static async Task AssertError(HttpClient client, string path, int status, string error, string key = DashboardKey)
        {
            var body = await Send(client, path, key, status);
            var property = Assert.Single(body.Properties());
            Assert.Equal("error", property.Name);
            Assert.Equal(error, (string)property.Value);
        }

        private static async Task<JObject> Send(HttpClient client, string path, string key = DashboardKey, int status = 200)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, path))
            {
                if (key != null) request.Headers.Add("X-Api-Key", key);
                using (var response = await client.SendAsync(request))
                {
                    Assert.Equal(status, (int)response.StatusCode);
                    var text = await response.Content.ReadAsStringAsync();
                    AssertNoKeys(text);
                    using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                        return JObject.Load(reader);
                }
            }
        }

        private static void AssertNoKeys(string text)
        {
            Assert.DoesNotContain(CollectorKey, text);
            Assert.DoesNotContain(DashboardKey, text);
            Assert.DoesNotContain(WrongKey, text);
        }

        private int InsertUser(string name, bool isActive)
        {
            return connection.QuerySingle<int>(@"
INSERT INTO dbo.APP_USER (Name, IsActive) OUTPUT INSERTED.UserId VALUES (@name, @isActive)", new { name, isActive });
        }

        private int InsertAlarm(int equipmentId, byte cause, DateTime raisedAtUtc,
            bool cleared = false, int? acknowledgedBy = null)
        {
            var parameters = new DynamicParameters(new { equipmentId, cause, acknowledgedBy });
            parameters.Add("raisedAtUtc", raisedAtUtc, DbType.DateTime2);
            parameters.Add("clearedAtUtc", cleared ? Now.AddMinutes(1) : (DateTime?)null, DbType.DateTime2);
            parameters.Add("acknowledgedAtUtc", acknowledgedBy.HasValue ? Now : (DateTime?)null, DbType.DateTime2);
            return connection.QuerySingle<int>(@"
INSERT INTO dbo.ALARM (EquipmentId, Cause, RaisedAtUtc, ClearedAtUtc, AcknowledgedAtUtc, AcknowledgedBy)
OUTPUT INSERTED.AlarmId VALUES (@equipmentId, @cause, @raisedAtUtc, @clearedAtUtc, @acknowledgedAtUtc, @acknowledgedBy)",
                parameters);
        }

        private int InsertFault(int alarmId, FaultStatus status, DateTime? recoveredAtUtc = null)
        {
            var parameters = new DynamicParameters(new { alarmId, status = (byte)status });
            parameters.Add("registeredAtUtc", Now.AddMinutes(1), DbType.DateTime2);
            parameters.Add("recoveredAtUtc", recoveredAtUtc, DbType.DateTime2);
            parameters.Add("closedAtUtc", recoveredAtUtc.HasValue ? Now.AddMinutes(2) : (DateTime?)null, DbType.DateTime2);
            return connection.QuerySingle<int>(@"
INSERT INTO dbo.FAULT (AlarmId, Status, RegisteredAtUtc, CauseCategory, CauseText, ActionText, RecoveredAtUtc, ClosedAtUtc)
OUTPUT INSERTED.FaultId
VALUES (@alarmId, @status, @registeredAtUtc, 1, N'cause text', N'action text', @recoveredAtUtc, @closedAtUtc)", parameters);
        }

        private void InsertHistory(int faultId, int userId, DateTime changedAtUtc,
            FaultStatus previousStatus, FaultStatus newStatus, string reason)
        {
            var parameters = new DynamicParameters(new
            {
                faultId, userId, previousStatus = (byte)previousStatus, newStatus = (byte)newStatus, reason
            });
            parameters.Add("changedAtUtc", changedAtUtc, DbType.DateTime2);
            connection.Execute(@"
INSERT INTO dbo.FAULT_HISTORY (FaultId, ChangedByUserId, ChangedAtUtc, PreviousStatus, NewStatus, Reason)
VALUES (@faultId, @userId, @changedAtUtc, @previousStatus, @newStatus, @reason)", parameters);
        }

        public void Dispose()
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
            }
        }
    }
}
