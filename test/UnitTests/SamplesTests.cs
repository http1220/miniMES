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
    public sealed class SamplesTests
    {
        private const string CollectorKey = "fake-collector-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string DashboardKey = "fake-dashboard-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string Time = "2026-11-06T01:02:03.123Z";

        private static JObject Body(int count = 1)
        {
            return new JObject { ["runId"] = "11111111-1111-1111-1111-111111111111",
                ["startedAtUtc"] = Time, ["samples"] = new JArray(Enumerable.Range(1, count).Select(i =>
                    new JObject { ["sequenceNumber"] = i, ["equipmentId"] = 1, ["temperature"] = 23.456,
                        ["vibration"] = 0, ["statusCode"] = 255, ["collectedAtUtc"] = Time })) };
        }

        [Theory]
        [InlineData(1)]
        [InlineData(25)]
        public void Validate_ValidBatch_PreservesOrderAndUtcMilliseconds(int count)
        {
            var body = Body(count);
            body["ignored"] = true;
            SamplesInput input;
            Assert.Null(SamplesInput.Validate(body, out input));
            Assert.Equal(count, input.Samples.Count);
            Assert.Equal(Guid.Parse((string)body["runId"]), input.RunId);
            Assert.Equal(DateTimeKind.Utc, input.StartedAtUtc.Kind);
            Assert.Equal(new DateTime(2026, 11, 6, 1, 2, 3, 123, DateTimeKind.Utc), input.StartedAtUtc);
            for (var i = 0; i < count; i++)
            {
                Assert.Equal((long)i + 1, input.Samples[i].SequenceNumber);
                Assert.Equal(input.RunId, input.Samples[i].RunId);
                Assert.Equal(input.StartedAtUtc, input.Samples[i].CollectedAtUtc);
                Assert.Equal((float?)23.456f, input.Samples[i].Temperature);
                Assert.Equal((float?)0f, input.Samples[i].Vibration);
                Assert.Equal((byte?)255, input.Samples[i].StatusCode);
                Assert.Equal(DateTimeKind.Utc, input.Samples[i].CollectedAtUtc.Kind);
            }
        }

        [Theory]
        [InlineData(0, "invalid_field")]
        [InlineData(26, "batch_too_large")]
        public void Validate_BatchSize_ReturnsError(int count, string error)
        {
            SamplesInput input;
            Assert.Equal(error, SamplesInput.Validate(Body(count), out input));
            Assert.Null(input);
        }

        public static IEnumerable<object[]> InvalidFields()
        {
            foreach (var field in new[] { "runId", "startedAtUtc", "samples", "sequenceNumber", "equipmentId",
                "collectedAtUtc", "temperature", "vibration", "statusCode" })
            {
                foreach (var value in new[] { "missing", "true", "{}", "\"1.5\"" })
                    yield return new object[] { field, value };
                if (field != "samples") yield return new object[] { field, "[]" };
                if (field != "temperature" && field != "vibration" && field != "statusCode")
                    yield return new object[] { field, "null" };
            }
            foreach (var field in new[] { "sequenceNumber", "equipmentId" })
            foreach (var value in new[] { "0", "-1", "1.0", "\"1\"", "9223372036854775808", "-9223372036854775809" })
                yield return new object[] { field, value };
            yield return new object[] { "equipmentId", "2147483648" };
            foreach (var field in new[] { "runId", "startedAtUtc", "collectedAtUtc", "samples" })
                yield return new object[] { field, "1" };
            foreach (var value in new[] { "-1", "256", "1.5" })
                yield return new object[] { "statusCode", value };
            foreach (var field in new[] { "temperature", "vibration" })
            foreach (var value in new[] { "NaN", "Infinity", "-Infinity", "3.5e38", "-3.5e38" })
                yield return new object[] { field, value };
            yield return new object[] { "runId", "\"00000000-0000-0000-0000-000000000000\"" };
            foreach (var field in new[] { "startedAtUtc", "collectedAtUtc" })
            foreach (var value in new[] { "2026-11-06T01:02:03.123", "2026-11-06T01:02:03.123+09:00",
                "2026-11-06T01:02:03.12Z", "2026-11-06T01:02:03.1234Z" })
                yield return new object[] { field, "\"" + value + "\"" };
        }

        [Theory]
        [MemberData(nameof(InvalidFields))]
        public void Validate_InvalidField_ReturnsInvalidFieldWithoutThrowing(string field, string value)
        {
            var body = Body();
            var owner = body.Property(field) != null ? body : (JObject)body["samples"][0];
            if (value == "missing") owner.Remove(field);
            else
            {
                using (var reader = new JsonTextReader(new StringReader(value)) { DateParseHandling = DateParseHandling.None })
                    owner[field] = JToken.ReadFrom(reader);
            }
            SamplesInput input;
            Assert.Equal("invalid_field", SamplesInput.Validate(body, out input));
            Assert.Null(input);
        }

        [Theory]
        [InlineData("3.4e38")]
        [InlineData("23")]
        [InlineData("9223372036854775808")]
        [InlineData("0")]
        [InlineData("null")]
        public void Validate_MeasurementsAndNullableStatus_AreAccepted(string value)
        {
            var body = Body();
            var item = (JObject)body["samples"][0];
            item["temperature"] = JToken.Parse(value);
            item["vibration"] = JToken.Parse(value);
            item["statusCode"] = JValue.CreateNull();
            item["sequenceNumber"] = long.MaxValue;
            item["equipmentId"] = int.MaxValue;
            SamplesInput input;
            Assert.Null(SamplesInput.Validate(body, out input));
            Assert.Equal(value == "null" ? (float?)null : (float)double.Parse(value,
                System.Globalization.CultureInfo.InvariantCulture), input.Samples[0].Temperature);
            Assert.Equal(input.Samples[0].Temperature, input.Samples[0].Vibration);
            Assert.Null(input.Samples[0].StatusCode);
            Assert.Equal(long.MaxValue, input.Samples[0].SequenceNumber);
            Assert.Equal(int.MaxValue, input.Samples[0].EquipmentId);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(255)]
        public void Validate_StatusBounds_AreAccepted(int status)
        {
            var body = Body();
            body["samples"][0]["statusCode"] = status;
            SamplesInput input;
            Assert.Null(SamplesInput.Validate(body, out input));
            Assert.Equal((byte?)status, input.Samples[0].StatusCode);
        }

        [Fact]
        public void Validate_NullRootAndNonObjectSample_ReturnInvalidField()
        {
            SamplesInput input;
            Assert.Equal("invalid_field", SamplesInput.Validate(null, out input));
            foreach (var token in new JToken[] { JValue.CreateNull(), new JValue(1), new JArray() })
            {
                var body = Body();
                body["samples"][0] = token;
                Assert.Equal("invalid_field", SamplesInput.Validate(body, out input));
            }
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
            using (var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/collector/samples"))
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
