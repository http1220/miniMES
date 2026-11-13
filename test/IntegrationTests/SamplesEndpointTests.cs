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
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Formatting.Compact;
using Server;
using Xunit;

namespace IntegrationTests
{
    public sealed class SamplesEndpointTests : IDisposable
    {
        private const string CollectorKey = "fake-collector-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string DashboardKey = "fake-dashboard-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string Endpoint = "/api/v1/collector/samples";
        private static readonly DateTime Now = new DateTime(2026, 11, 13, 1, 2, 3, 456, DateTimeKind.Utc);
        private readonly string databaseName = "MiniMES_SamplesEndpoint_" + Guid.NewGuid().ToString("N");
        private readonly string directory = Path.Combine(Path.GetTempPath(), "samples-endpoint-" + Guid.NewGuid().ToString("N"));
        private readonly SqlConnection admin;
        private readonly SqlConnection connection;
        private readonly string connectionString;
        private readonly List<string> usedConnections = new List<string>();

        public SamplesEndpointTests()
        {
            var configured = Environment.GetEnvironmentVariable("MINIMES_TEST_SQL_CONNECTION_STRING");
            Assert.False(string.IsNullOrWhiteSpace(configured), "Set MINIMES_TEST_SQL_CONNECTION_STRING for a test SQL Server with CREATE DATABASE permission.");
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
            }
            catch { Dispose(); throw; }
        }

        private static JObject Body(Guid runId, params int[] equipmentIds)
        {
            return new JObject { ["runId"] = runId.ToString(), ["startedAtUtc"] = "2026-11-06T01:02:03.123Z",
                ["samples"] = new JArray(equipmentIds.Select((id, index) => new JObject {
                    ["sequenceNumber"] = index + 1, ["equipmentId"] = id, ["temperature"] = index == 0 ? (JToken)new JValue(23.456) : JValue.CreateNull(),
                    ["vibration"] = index == 0 ? (JToken)new JValue(0.125) : JValue.CreateNull(),
                    ["statusCode"] = index == 0 ? (JToken)new JValue(1) : JValue.CreateNull(), ["collectedAtUtc"] = "2026-11-06T01:02:04.123Z" })) };
        }

        private Harness CreateHarness(string selectedConnection = null)
        {
            var selected = selectedConnection ?? connectionString;
            usedConnections.Add(selected);
            return new Harness(selected, Path.Combine(directory, Guid.NewGuid().ToString("N") + ".log"));
        }

        [Fact]
        public async Task Post_StoreAndReplay_PreservesValuesAndFirstRecordedTime()
        {
            var runId = Guid.NewGuid();
            var body = Body(runId, 1, 1);
            string logPath;
            using (var harness = CreateHarness())
            {
                logPath = harness.LogPath;
                var first = await harness.Send(body, 200);
                Assert.Equal(2, (int)first["storedSamples"]);
                Assert.Equal(0, (int)first["duplicateSamples"]);
                Assert.Equal("2026-11-13T01:02:03.456Z", (string)first["serverTimeUtc"]);
                Assert.Equal(1, harness.ClockReads);
                var rows = connection.Query<StoredSample>("SELECT SequenceNumber, Temperature, Vibration, StatusCode, RecordedAtUtc FROM dbo.SAMPLE ORDER BY SequenceNumber").ToArray();
                Assert.Equal(2, rows.Length);
                Assert.Equal((float?)23.456f, rows[0].Temperature);
                Assert.Equal((float?)0.125f, rows[0].Vibration);
                Assert.Equal((byte?)1, rows[0].StatusCode);
                Assert.Null(rows[1].Temperature);
                Assert.Null(rows[1].Vibration);
                Assert.Null(rows[1].StatusCode);
                Assert.All(rows, row => Assert.Equal(Now, row.RecordedAtUtc));
                Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.COLLECTOR_RUN WHERE RunId=@runId", new { runId }));
                harness.CurrentTime = Now.AddHours(1);
                var replay = await harness.Send(body, 200);
                Assert.Equal(0, (int)replay["storedSamples"]);
                Assert.Equal(2, (int)replay["duplicateSamples"]);
                Assert.Equal("2026-11-13T02:02:03.456Z", (string)replay["serverTimeUtc"]);
                Assert.Equal(2, harness.ClockReads);
                Assert.Equal(2, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SAMPLE"));
                Assert.All(connection.Query<DateTime>("SELECT RecordedAtUtc FROM dbo.SAMPLE"), time => Assert.Equal(Now, time));
                Assert.Equal("checking", harness.Database.State);
            }
            var logs = Logs(logPath);
            Assert.Equal(2, logs.Length);
            for (var i = 0; i < logs.Length; i++)
            {
                Assert.Equal("Collector success {Endpoint} {RunId} {MinSequence} {MaxSequence} {StoredSamples} {DuplicateSamples}", (string)logs[i]["@mt"]);
                Assert.Equal(Endpoint, (string)logs[i]["Endpoint"]);
                Assert.Equal(runId, Guid.Parse((string)logs[i]["RunId"]));
                Assert.Equal(1, (long)logs[i]["MinSequence"]);
                Assert.Equal(2, (long)logs[i]["MaxSequence"]);
                Assert.Equal(i == 0 ? 2 : 0, (int)logs[i]["StoredSamples"]);
                Assert.Equal(i == 0 ? 0 : 2, (int)logs[i]["DuplicateSamples"]);
                Assert.Null(logs[i]["@l"]);
            }
        }

        private sealed class StoredSample
        {
            public long SequenceNumber { get; set; }
            public float? Temperature { get; set; }
            public float? Vibration { get; set; }
            public byte? StatusCode { get; set; }
            public DateTime RecordedAtUtc { get; set; }
        }

        [Fact]
        public async Task Post_MissingEquipment_RollsBackRunAndAllSamplesAndLogsSqlNumber()
        {
            var runId = Guid.NewGuid();
            string logPath;
            using (var harness = CreateHarness())
            {
                logPath = harness.LogPath;
                var response = await harness.Send(Body(runId, 1, 999), 400);
                Assert.Equal("invalid_field", (string)response["error"]);
                Assert.Equal("checking", harness.Database.State);
            }
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SAMPLE"));
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.COLLECTOR_RUN WHERE RunId=@runId", new { runId }));
            var log = Assert.Single(Logs(logPath));
            Assert.Equal("Warning", (string)log["@l"]);
            Assert.Equal(547, (int)log["SqlNumber"]);
            Assert.Equal("Microsoft.Data.SqlClient.SqlException", (string)log["ExceptionType"]);
            Assert.Equal(Endpoint, (string)log["Endpoint"]);
            Assert.Equal("invalid_field", (string)log["Error"]);
            Assert.Equal(runId, Guid.Parse((string)log["RunId"]));
            Assert.Equal(1, (long)log["MinSequence"]);
            Assert.Equal(2, (long)log["MaxSequence"]);
        }

        [Fact]
        public async Task Post_UnreachableDatabase_ReturnsUnavailableAndMarksDisconnected()
        {
            var builder = new SqlConnectionStringBuilder(connectionString) {
                DataSource = "tcp:127.0.0.1,1", ConnectTimeout = 2 };
            string logPath;
            using (var harness = CreateHarness(builder.ConnectionString))
            {
                logPath = harness.LogPath;
                var response = await harness.Send(Body(Guid.NewGuid(), 1), 503);
                Assert.Equal("database_unavailable", (string)response["error"]);
                Assert.Equal("disconnected", harness.Database.State);
            }
            var log = Assert.Single(Logs(logPath));
            Assert.Equal("Error", (string)log["@l"]);
            Assert.Equal("database_unavailable", (string)log["Error"]);
            Assert.Equal("Microsoft.Data.SqlClient.SqlException", (string)log["ExceptionType"]);
            Assert.Equal(Endpoint, (string)log["Endpoint"]);
            Assert.NotEqual(JTokenType.Null, log["SqlNumber"].Type);
        }

        private static JObject[] Logs(string path)
        {
            return File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).Select(JObject.Parse).ToArray();
        }

        private sealed class Harness : IDisposable
        {
            public readonly string LogPath;
            public readonly DatabaseMonitor Database;
            public DateTime CurrentTime = Now;
            public int ClockReads { get; private set; }
            private readonly Serilog.Core.Logger logger;
            private readonly HttpConfiguration configuration;
            private readonly HttpClient client;

            public Harness(string selectedConnection, string logPath)
            {
                LogPath = logPath;
                ServerSettings settings;
                Assert.Empty(ServerSettings.Validate(new Dictionary<string, string> {
                    ["SERVER_URL"] = "https://+:5443/", ["DB_CONNECTION_STRING"] = selectedConnection,
                    ["COLLECTOR_API_KEY"] = CollectorKey, ["DASHBOARD_API_KEY"] = DashboardKey }, out settings));
                logger = new LoggerConfiguration().WriteTo.File(new CompactJsonFormatter(), logPath).CreateLogger();
                Database = new DatabaseMonitor(() => false, TimeSpan.FromSeconds(2));
                configuration = ApiConfiguration.Create(new ApiRuntime(settings,
                    new CollectorState(() => TimeSpan.Zero, TimeSpan.FromSeconds(10)), Database, () => { ClockReads++; return CurrentTime; }, logger));
                client = new HttpClient(new HttpServer(configuration)) { BaseAddress = new Uri("http://localhost/") };
            }

            public async Task<JObject> Send(JObject body, int expectedStatus)
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, Endpoint))
                {
                    request.Headers.Add("X-Api-Key", CollectorKey);
                    request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
                    using (var response = await client.SendAsync(request))
                    {
                        Assert.Equal(expectedStatus, (int)response.StatusCode);
                        var text = await response.Content.ReadAsStringAsync();
                        Assert.DoesNotContain(CollectorKey, text);
                        Assert.DoesNotContain(DashboardKey, text);
                        using (var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(text))
                            { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
                            return JObject.Load(reader);
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

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(directory)) AssertLogsContainNoSecrets();
            }
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
