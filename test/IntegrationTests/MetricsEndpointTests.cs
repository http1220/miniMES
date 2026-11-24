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
    public sealed class MetricsEndpointTests : IDisposable
    {
        private const string CollectorKey = "change-me-collector-placeholder-key";
        private const string DashboardKey = "change-me-dashboard-placeholder-key";
        private const string WrongKey = "change-me-incorrect-placeholder-key";
        private const string Metrics = "/api/v1/metrics";
        private static readonly DateTime From = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime To = From.AddSeconds(30);
        private static readonly DateTime Now = From.AddDays(15);
        private readonly string databaseName = "MiniMES_Metrics_" + Guid.NewGuid().ToString("N");
        private readonly SqlConnection admin;
        private readonly SqlConnection connection;
        private readonly string connectionString;

        public MetricsEndpointTests()
        {
            var configured = Environment.GetEnvironmentVariable("MINIMES_TEST_SQL_CONNECTION_STRING");
            Assert.False(string.IsNullOrWhiteSpace(configured),
                "Set MINIMES_TEST_SQL_CONNECTION_STRING to a test SQL Server connection string with CREATE DATABASE permission.");
            var builder = new SqlConnectionStringBuilder(configured)
            {
                InitialCatalog = "master",
                Pooling = false,
                MultipleActiveResultSets = false
            };
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
        public async Task KeyMatrixAndTiming_EnforcesAccessAndLogsOnlySafeFields()
        {
            var logPath = Path.Combine(Path.GetTempPath(), "metrics-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                using (var logger = new LoggerConfiguration().WriteTo.File(new CompactJsonFormatter(), logPath).CreateLogger())
                using (var database = new DatabaseMonitor(() => false, TimeSpan.FromSeconds(2)))
                using (var client = CreateClient(database, logger))
                {
                    foreach (var endpoint in new[] { Metrics, SystemPath("2") })
                    {
                        var path = endpoint + Period(From, To);
                        await AssertError(client, path, 401, "unauthorized", null);
                        await AssertError(client, path, 401, "unauthorized", WrongKey);
                        await AssertError(client, path, 403, "forbidden", CollectorKey);
                        await Send(client, path);
                    }
                }

                var logs = File.ReadAllText(logPath);
                AssertNoKeys(logs);
                Assert.DoesNotContain(connectionString, logs);
                var timings = File.ReadLines(logPath).Select(line => JObject.Parse(line))
                    .Where(evt => (string)evt["@mt"] == "Metrics computed {Endpoint} {PeriodSeconds} {EquipmentCount} {ElapsedMs}")
                    .ToArray();
                Assert.Equal(2, timings.Length);
                Assert.Equal(Metrics, (string)timings[0]["Endpoint"]);
                Assert.Equal("/api/v1/systems/{systemId}/availability", (string)timings[1]["Endpoint"]);
                Assert.Equal(5, (int)timings[0]["EquipmentCount"]);
                Assert.Equal(1, (int)timings[1]["EquipmentCount"]);
                Assert.All(timings, timing =>
                {
                    Assert.Equal(30.0, (double)timing["PeriodSeconds"]);
                    Assert.True((long)timing["ElapsedMs"] >= 0);
                });
            }
            finally
            {
                if (File.Exists(logPath)) File.Delete(logPath);
            }
        }

        [Fact]
        public async Task Metrics_MapsHandCalculatedResultsAndUnavailableEquipment()
        {
            InsertSamples(1);
            InsertClosedFault(From.AddSeconds(3), From.AddSeconds(17));

            using (var database = new DatabaseMonitor(() => false, TimeSpan.FromSeconds(2)))
            using (var client = CreateClient(database))
            {
                var body = await Send(client, Metrics + Period(From, To));
                Assert.Equal(UtcText(From), (string)body["fromUtc"]);
                Assert.Equal(UtcText(To), (string)body["toUtc"]);
                Assert.Equal(UtcText(Now), (string)body["serverTimeUtc"]);

                var equipment = (JArray)body["equipment"];
                Assert.Equal(Enumerable.Range(1, 5), equipment.Select(row => (int)row["equipmentId"]));
                var first = equipment[0];
                // T=5초. [5,15)는 공백 10초, [15,20)은 이상 상태 5초,
                // 나머지 15초는 가동. 관측 20초, 고장 한 건이므로 MTBF=15초.
                // 고장 [3,17)은 복구까지 14초. 관측과 겹치는 [3,5), [15,17)은 총 4초라 가용도=(20-4)/20.
                Assert.Equal(15.0, (double)first["uptimeSeconds"]);
                Assert.Equal(10.0, (double)first["gapSeconds"]);
                Assert.Equal(20.0, (double)first["observedSeconds"]);
                Assert.Equal(15.0, (double)first["mtbfSeconds"]);
                Assert.Equal(14.0, (double)first["mttrSeconds"]);
                Assert.Equal(0.8, (double)first["availability"]);
                Assert.Equal(1, (int)first["faultCount"]);
                Assert.Equal(1, (int)first["closedFaultCount"]);
                Assert.Equal(0, (int)first["unclosedFaultCount"]);
                foreach (var metric in new[] { "mtbf", "mttr", "availability" })
                    Assert.Equal(JTokenType.Null, first["unavailable"][metric].Type);

                Assert.All(equipment.Skip(1), row =>
                {
                    Assert.Equal(JTokenType.Null, row["availability"].Type);
                    Assert.Equal("zero_observed_time", (string)row["unavailable"]["availability"]["reason"]);
                    Assert.Equal("zero_observed_time", (string)row["unavailable"]["availability"]["detail"]);
                    Assert.Equal(JTokenType.Null, row["mtbfSeconds"].Type);
                    Assert.Equal("no_faults", (string)row["unavailable"]["mtbf"]["reason"]);
                    Assert.Equal(JTokenType.Null, row["mttrSeconds"].Type);
                    Assert.Equal("no_closed_faults", (string)row["unavailable"]["mttr"]["reason"]);
                });
            }
        }

        [Fact]
        public async Task Metrics_ReportsInvalidFaultDataAsInternalError()
        {
            InsertClosedFault(From.AddSeconds(17), From.AddSeconds(3));
            var logPath = Path.Combine(Path.GetTempPath(), "metrics-failure-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                using (var logger = new LoggerConfiguration().WriteTo.File(new CompactJsonFormatter(), logPath).CreateLogger())
                using (var database = new DatabaseMonitor(() => false, TimeSpan.FromSeconds(2)))
                using (var client = CreateClient(database, logger))
                    await AssertError(client, Metrics + Period(From, To), 500, "internal");

                var log = JObject.Parse(File.ReadAllText(logPath));
                Assert.Equal("Dashboard failure {Endpoint} {Error} {ExceptionType} {SqlNumber}", (string)log["@mt"]);
                Assert.Equal(Metrics, (string)log["Endpoint"]);
                Assert.Equal("internal", (string)log["Error"]);
                Assert.Equal(typeof(ArgumentException).FullName, (string)log["ExceptionType"]);
                Assert.Null(log["@x"]);
                AssertNoKeys(log.ToString());
            }
            finally
            {
                if (File.Exists(logPath)) File.Delete(logPath);
            }
        }

        [Fact]
        public async Task Period_BothEndpointsRejectInvalidQueriesAndAllowFourteenDays()
        {
            using (var database = new DatabaseMonitor(() => false, TimeSpan.FromSeconds(2)))
            using (var client = CreateClient(database))
            {
                foreach (var endpoint in new[] { Metrics, SystemPath("1") })
                {
                    var valid = Period(From, To);
                    foreach (var query in new[]
                    {
                        "", "?fromUtc=" + UtcText(From), "?toUtc=" + UtcText(To),
                        "?fromUtc=2026-11-01T00:00:00Z&toUtc=" + UtcText(To),
                        valid + "&fromUtc=" + UtcText(From), valid + "&toUtc=" + UtcText(To),
                        Period(From, From), Period(To, From),
                        Period(From, From.AddDays(14).AddMilliseconds(1)),
                        Period(Now.AddDays(-1), Now.AddMilliseconds(1))
                    })
                        await AssertError(client, endpoint + query, 400, "invalid_field");

                    await Send(client, endpoint + Period(Now.AddDays(-14), Now));
                }
            }
        }

        [Fact]
        public async Task SystemAvailability_ReportsUnavailableChildAndComputesSeededSystems()
        {
            using (var database = new DatabaseMonitor(() => false, TimeSpan.FromSeconds(2)))
            using (var client = CreateClient(database))
            {
                var period = Period(From, To);
                var single = await Send(client, SystemPath("2") + period);
                Assert.Equal(2, (int)single["systemId"]);
                Assert.Equal("시스템 2", (string)single["name"]);
                Assert.Equal(UtcText(Now), (string)single["serverTimeUtc"]);
                Assert.Equal(JTokenType.Null, single["availability"].Type);
                Assert.Equal("zero_observed_time", (string)single["unavailable"]["reason"]);

                var group = await Send(client, SystemPath("1") + period);
                Assert.Equal(JTokenType.Null, group["availability"].Type);
                Assert.Equal("child_unavailable", (string)group["unavailable"]["reason"]);
                Assert.Equal("equipment=1", (string)group["unavailable"]["detail"]);

                for (int id = 1; id <= 4; id++)
                    InsertSamples(id);
                group = await Send(client, SystemPath("1") + period);
                Assert.Equal(1, (int)group["systemId"]);
                Assert.Equal("시스템 1", (string)group["name"]);
                Assert.Equal(1.0, (double)group["availability"]);
                Assert.Equal(JTokenType.Null, group["unavailable"].Type);

                InsertSamples(5);
                InsertClosedFault(From.AddSeconds(3), From.AddSeconds(17), equipmentId: 5);
                single = await Send(client, SystemPath("2") + period);
                Assert.Equal(0.8, (double)single["availability"]);
                Assert.Equal(JTokenType.Null, single["unavailable"].Type);
            }
        }

        [Fact]
        public async Task SystemAvailability_ValidatesPeriodAndSystemIdBeforeReading()
        {
            using (var database = new DatabaseMonitor(() => false, TimeSpan.FromSeconds(2)))
            using (var client = CreateClient(database))
            {
                var period = Period(From, To);
                await AssertError(client, SystemPath("abc") + period, 400, "invalid_field");
                await AssertError(client, SystemPath("0") + period, 400, "invalid_field");
                await AssertError(client, SystemPath("999"), 400, "invalid_field");
                await AssertError(client, SystemPath("999") + period, 404, "not_found");
            }
        }

        [Fact]
        public async Task SystemAvailability_ReportsStructureErrorForEmptyParallelGroup()
        {
            var parallelNode = connection.QuerySingle<int>(
                "SELECT NodeId FROM dbo.SYSTEM_NODE WHERE SystemId = 1 AND NodeType = 3");
            connection.Execute("DELETE FROM dbo.SYSTEM_NODE WHERE SystemId = 1 AND EquipmentId IN (2, 3, 4)");
            connection.Execute("DELETE FROM dbo.SYSTEM_NODE WHERE ParentNodeId = @parallelNode", new { parallelNode });

            using (var database = new DatabaseMonitor(() => false, TimeSpan.FromSeconds(2)))
            using (var client = CreateClient(database))
            {
                var body = await Send(client, SystemPath("1") + Period(From, To));
                Assert.Equal(JTokenType.Null, body["availability"].Type);
                Assert.Equal("structure_error", (string)body["unavailable"]["reason"]);
                Assert.Equal("group_has_no_children node=" + parallelNode.ToString(CultureInfo.InvariantCulture),
                    (string)body["unavailable"]["detail"]);
            }
        }

        private void InsertSamples(int equipmentId)
        {
            var parameters = new DynamicParameters(new { equipmentId, runId = Guid.NewGuid() });
            parameters.Add("from", From, DbType.DateTime2);
            connection.Execute(@"
INSERT INTO dbo.COLLECTOR_RUN (RunId, StartedAtUtc) VALUES (@runId, @from);
INSERT INTO dbo.SAMPLE
    (RunId, SequenceNumber, EquipmentId, CollectedAtUtc, RecordedAtUtc, Temperature, Vibration, StatusCode)
SELECT @runId, SequenceNumber, @equipmentId, DATEADD(second, OffsetSeconds, @from),
       DATEADD(second, OffsetSeconds, @from), 40, 1, StatusCode
FROM (VALUES (1, 0, 1), (2, 5, 1), (3, 15, 2), (4, 20, 1), (5, 25, 1), (6, 30, 1))
    AS Fixture(SequenceNumber, OffsetSeconds, StatusCode);", parameters);
        }

        private void InsertClosedFault(DateTime raisedAtUtc, DateTime recoveredAtUtc, int equipmentId = 1)
        {
            var parameters = new DynamicParameters(new { equipmentId });
            parameters.Add("raisedAtUtc", raisedAtUtc, DbType.DateTime2);
            parameters.Add("recoveredAtUtc", recoveredAtUtc, DbType.DateTime2);
            connection.Execute(@"
INSERT INTO dbo.ALARM (EquipmentId, Cause, RaisedAtUtc) VALUES (@equipmentId, 1, @raisedAtUtc);
INSERT INTO dbo.FAULT (AlarmId, Status, RegisteredAtUtc, CauseCategory, RecoveredAtUtc, ClosedAtUtc)
VALUES (SCOPE_IDENTITY(), 3, @raisedAtUtc, 1, @recoveredAtUtc, @recoveredAtUtc);", parameters);
        }

        private static string UtcText(DateTime utc)
        {
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        private static string Period(DateTime fromUtc, DateTime toUtc)
        {
            return "?fromUtc=" + UtcText(fromUtc) + "&toUtc=" + UtcText(toUtc);
        }

        private static string SystemPath(string id)
        {
            return "/api/v1/systems/" + id + "/availability";
        }

        private HttpClient CreateClient(DatabaseMonitor database, ILogger logger = null)
        {
            ServerSettings settings;
            Assert.Empty(ServerSettings.Validate(new Dictionary<string, string>
            {
                ["SERVER_URL"] = "https://+:5443/",
                ["DB_CONNECTION_STRING"] = connectionString,
                ["COLLECTOR_API_KEY"] = CollectorKey,
                ["DASHBOARD_API_KEY"] = DashboardKey,
                ["GAP_THRESHOLD_SECONDS"] = "5"
            }, out settings));
            var runtime = new ApiRuntime(settings, new CollectorState(() => TimeSpan.Zero, TimeSpan.FromSeconds(10)),
                database, () => Now.AddTicks(7000), logger ?? Log.Logger);
            return new HttpClient(new HttpServer(ApiConfiguration.Create(runtime))) { BaseAddress = new Uri("http://localhost/") };
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
