using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using Server;
using Xunit;

namespace UnitTests
{
    public sealed class AlarmEventsTests
    {
        private const string CollectorKey = "fake-collector-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string DashboardKey = "fake-dashboard-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string Time = "2026-11-06T01:02:03.123Z";

        private static JObject Body(int count = 1)
        {
            return new JObject { ["runId"] = "11111111-1111-1111-1111-111111111111",
                ["startedAtUtc"] = Time, ["events"] = new JArray(Enumerable.Range(1, count).Select(i =>
                    new JObject { ["eventSequence"] = i, ["equipmentId"] = 1, ["cause"] = 1,
                        ["eventType"] = 1, ["occurredAtUtc"] = Time })) };
        }

        [Theory]
        [InlineData(1)]
        [InlineData(25)]
        public void Validate_ValidBatch_PreservesOrderAndUtcMilliseconds(int count)
        {
            var body = Body(count);
            body["ignored"] = true;
            AlarmEventsInput input;
            Assert.Null(AlarmEventsInput.Validate(body, out input));
            Assert.Equal(count, input.Events.Count);
            Assert.Equal(Guid.Parse((string)body["runId"]), input.RunId);
            Assert.Equal(DateTimeKind.Utc, input.StartedAtUtc.Kind);
            Assert.Equal(new DateTime(2026, 11, 6, 1, 2, 3, 123, DateTimeKind.Utc), input.StartedAtUtc);
            for (var i = 0; i < count; i++)
            {
                Assert.Equal((long)i + 1, input.Events[i].EventSequence);
                Assert.Equal(input.RunId, input.Events[i].RunId);
                Assert.Equal(input.StartedAtUtc, input.Events[i].OccurredAtUtc);
                Assert.Equal(DateTimeKind.Utc, input.Events[i].OccurredAtUtc.Kind);
            }
        }

        [Theory]
        [InlineData(0, "invalid_field")]
        [InlineData(26, "batch_too_large")]
        public void Validate_BatchSize_ReturnsError(int count, string error)
        {
            AlarmEventsInput input;
            Assert.Equal(error, AlarmEventsInput.Validate(Body(count), out input));
            Assert.Null(input);
        }

        public static IEnumerable<object[]> InvalidFields()
        {
            var fields = new[] { "runId", "startedAtUtc", "events", "eventSequence", "equipmentId", "cause", "eventType", "occurredAtUtc" };
            foreach (var field in fields)
            {
                yield return new object[] { field, "missing" };
                yield return new object[] { field, "null" };
                yield return new object[] { field, "true" };
                yield return new object[] { field, "{}" };
                if (field == "runId" || field == "startedAtUtc" || field == "occurredAtUtc" || field == "events")
                    yield return new object[] { field, "1" };
                if (field != "events") yield return new object[] { field, "[]" };
            }
            foreach (var field in new[] { "eventSequence", "equipmentId", "cause", "eventType" })
            foreach (var value in new[] { "0", "-1", "1.0", "\"1\"", "9223372036854775808", "-9223372036854775809" })
                yield return new object[] { field, value };
            yield return new object[] { "events", "\"events\"" };
            yield return new object[] { "equipmentId", "2147483648" };
            yield return new object[] { "cause", "4" };
            yield return new object[] { "eventType", "5" };
            yield return new object[] { "runId", "\"00000000-0000-0000-0000-000000000000\"" };
            yield return new object[] { "runId", "\"invalid\"" };
            foreach (var field in new[] { "startedAtUtc", "occurredAtUtc" })
            foreach (var value in new[] { "2026-11-06T01:02:03.123", "2026-11-06T01:02:03.123+00:00",
                "2026-11-06T01:02:03.123+09:00", "2026-11-06T01:02:03Z", "2026-11-06T01:02:03.1Z",
                "2026-11-06T01:02:03.12Z", "2026-11-06T01:02:03.1234Z", "2026-02-30T01:02:03.123Z" })
                yield return new object[] { field, "\"" + value + "\"" };
        }

        [Theory]
        [MemberData(nameof(InvalidFields))]
        public void Validate_InvalidField_ReturnsInvalidFieldWithoutThrowing(string field, string value)
        {
            var body = Body();
            var owner = body.Property(field) != null ? body : (JObject)body["events"][0];
            if (value == "missing") owner.Remove(field);
            else
            {
                using (var reader = new JsonTextReader(new StringReader(value)) { DateParseHandling = DateParseHandling.None })
                    owner[field] = JToken.ReadFrom(reader);
            }
            AlarmEventsInput input;
            Assert.Equal("invalid_field", AlarmEventsInput.Validate(body, out input));
            Assert.Null(input);
        }

        [Fact]
        public void Validate_IntegerUpperBoundsAndEnums_AreAccepted()
        {
            foreach (var cause in new[] { 1, 3 })
            foreach (var type in new[] { 1, 4 })
            {
                var body = Body();
                var item = (JObject)body["events"][0];
                item["eventSequence"] = long.MaxValue;
                item["equipmentId"] = int.MaxValue;
                item["cause"] = cause;
                item["eventType"] = type;
                AlarmEventsInput input;
                Assert.Null(AlarmEventsInput.Validate(body, out input));
                Assert.Equal(long.MaxValue, input.Events[0].EventSequence);
                Assert.Equal(int.MaxValue, input.Events[0].EquipmentId);
            }
        }

        [Fact]
        public void Validate_NullRootAndNonObjectEvent_ReturnInvalidField()
        {
            AlarmEventsInput input;
            Assert.Equal("invalid_field", AlarmEventsInput.Validate(null, out input));
            foreach (var token in new JToken[] { JValue.CreateNull(), new JValue(1), new JArray() })
            {
                var body = Body();
                body["events"][0] = token;
                Assert.Equal("invalid_field", AlarmEventsInput.Validate(body, out input));
            }
        }

        [Theory]
        [InlineData(547, 400)]
        [InlineData(2601, 400)]
        [InlineData(2627, 400)]
        [InlineData(8152, 400)]
        [InlineData(-2, 503)]
        [InlineData(53, 503)]
        [InlineData(1205, 503)]
        [InlineData(208, 503)]
        [InlineData(2628, 503)]
        [InlineData(515, 503)]
        public void ClassifySql_UsesOnlySpecifiedClientErrorNumbers(int number, int status)
        {
            Assert.Equal(status, (int)CollectorFailure.ClassifySql(number));
        }

        [Fact]
        public void ClassifyNonSql_ReturnsInternalError()
        {
            Assert.Equal(HttpStatusCode.InternalServerError, CollectorFailure.ClassifyNonSql(new InvalidOperationException()));
        }

        [Theory]
        [InlineData("large", 400, "batch_too_large")]
        [InlineData("json", 400, "invalid_json")]
        [InlineData("empty", 400, "invalid_field")]
        [InlineData("array", 400, "invalid_field")]
        [InlineData("none", 400, "invalid_json")]
        [InlineData("dashboard", 403, "forbidden")]
        [InlineData("missing", 401, "unauthorized")]
        public async Task Post_RejectedBeforeDatabase_DoesNotChangeDatabaseState(string scenario, int status, string error)
        {
            ServerSettings settings;
            Assert.Empty(ServerSettings.Validate(new Dictionary<string, string> {
                ["SERVER_URL"] = "https://+:5443/", ["DB_CONNECTION_STRING"] = "Server=example.invalid;Database=fake;Integrated Security=True",
                ["COLLECTOR_API_KEY"] = CollectorKey, ["DASHBOARD_API_KEY"] = DashboardKey }, out settings));
            using (var logger = new LoggerConfiguration().CreateLogger())
            using (var database = new DatabaseMonitor(() => { throw new InvalidOperationException(); }, TimeSpan.FromSeconds(2)))
            using (var configuration = ApiConfiguration.Create(new ApiRuntime(settings,
                new CollectorState(() => TimeSpan.Zero, TimeSpan.FromSeconds(10)), database, () => DateTime.UtcNow, logger)))
            using (var client = new HttpClient(new HttpServer(configuration)) { BaseAddress = new Uri("http://localhost/") })
            using (var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/collector/alarm-events"))
            {
                if (scenario != "missing") request.Headers.Add("X-Api-Key", scenario == "dashboard" ? DashboardKey : CollectorKey);
                var content = new UnreadableContent();
                if (scenario == "large") request.Content = content;
                else if (scenario != "none") request.Content = new StringContent(scenario == "json" ? "not json" :
                    scenario == "array" ? "[]" : Body(0).ToString(), Encoding.UTF8, "application/json");
                using (var response = await client.SendAsync(request))
                {
                    Assert.Equal(status, (int)response.StatusCode);
                    Assert.Equal(error, (string)JObject.Parse(await response.Content.ReadAsStringAsync())["error"]);
                }
                Assert.False(content.Read);
                content.Dispose();
                Assert.Equal("checking", database.State);
            }
        }

        private sealed class UnreadableContent : HttpContent
        {
            public bool Read { get; private set; }
            protected override bool TryComputeLength(out long length) { length = 1048577; return true; }
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                Read = true;
                throw new InvalidOperationException("Body must not be read.");
            }
        }
    }
}
