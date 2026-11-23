using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Collector;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace UnitTests
{
    public class HttpServerClientTests
    {
        private const string ApiKey = "change-me-collector-http-test-key";
        private const string ServerTime = "2026-11-22T12:00:00.123Z";
        private static readonly Guid RunId = new Guid("01234567-89ab-cdef-0123-456789abcdef");
        private static readonly DateTime Noon = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SendRequests_UseContractPathsHeadersAndJson(bool trailingSlash)
        {
            var handler = new RecordingHandler((_, __) => Task.FromResult(Response(200, SuccessBody)));
            var baseUrl = new Uri("https://collector.example.test/prefix" + (trailingSlash ? "/" : ""));
            using (var client = CreateClient(handler, baseUrl: baseUrl))
            {
                var alarm = new ConfirmedAlarmEvent(new AlarmEventItem(12, 1, AlarmCause.Temperature,
                    AlarmEventType.Cleared, TimeSpan.Zero), Noon.AddTicks(4569999));
                var sample = new ConfirmedSample(new SampleItem(2, 340, TimeSpan.Zero, null, 0.125f, null),
                    Noon.AddTicks(9879999));
                var startedAt = Noon.AddTicks(1239999).ToLocalTime();

                Assert.Equal(ServerOutcome.Success, (await client.SendAlarmEventsAsync(RunId, startedAt,
                    new[] { alarm }, CancellationToken.None)).Outcome);
                AssertRequest(handler, "alarm-events",
                    @"{""runId"":""01234567-89ab-cdef-0123-456789abcdef"",""startedAtUtc"":""2026-11-22T12:00:00.123Z"",
                       ""events"":[{""eventSequence"":12,""equipmentId"":1,""cause"":2,""eventType"":2,
                                    ""occurredAtUtc"":""2026-11-22T12:00:00.456Z""}]}");

                Assert.Equal(ServerOutcome.Success, (await client.SendSamplesAsync(RunId, startedAt,
                    new[] { sample }, CancellationToken.None)).Outcome);
                AssertRequest(handler, "samples",
                    @"{""runId"":""01234567-89ab-cdef-0123-456789abcdef"",""startedAtUtc"":""2026-11-22T12:00:00.123Z"",
                       ""samples"":[{""equipmentId"":2,""sequenceNumber"":340,""collectedAtUtc"":""2026-11-22T12:00:00.987Z"",
                                     ""temperature"":null,""vibration"":0.125,""statusCode"":null}]}");

                Assert.Equal(ServerOutcome.Success, (await client.SendHeartbeatAsync(RunId, CancellationToken.None)).Outcome);
                AssertRequest(handler, "heartbeat", @"{""runId"":""01234567-89ab-cdef-0123-456789abcdef""}");
            }
        }

        [Fact]
        public async Task Success_CapturesMonotonicTimeAfterBodyIsBuffered()
        {
            var monotonic = TimeSpan.Zero;
            var bodyReceivedAt = TimeSpan.FromSeconds(12);
            var handler = new RecordingHandler((_, __) =>
            {
                monotonic = TimeSpan.FromSeconds(10);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ObservedContent(SuccessBody, () => monotonic = bodyReceivedAt)
                });
            });
            using (var client = CreateClient(handler, now: () => monotonic))
            {
                var result = await client.SendHeartbeatAsync(RunId, CancellationToken.None);

                Assert.Equal(ServerOutcome.Success, result.Outcome);
                Assert.Equal(200, result.StatusCode);
                Assert.Equal(bodyReceivedAt, result.ReceivedAt);
                Assert.Equal(Noon.AddMilliseconds(123), result.ServerTimeUtc);
                Assert.Equal(DateTimeKind.Utc, result.ServerTimeUtc.Value.Kind);
                Assert.Null(result.ErrorKind);
            }
        }

        [Theory]
        [InlineData(400, @"{""error"":""invalid_field""}", ServerOutcome.BadRequest, "invalid_field")]
        [InlineData(400, @"{""error"":""<script>""}", ServerOutcome.BadRequest, null)]
        [InlineData(400, @"{""error"":""invalid_field\n""}", ServerOutcome.BadRequest, null)]
        [InlineData(400, @"{""error"":42}", ServerOutcome.BadRequest, null)]
        [InlineData(400, "{", ServerOutcome.BadRequest, null)]
        [InlineData(401, "{}", ServerOutcome.AuthFailed, null)]
        [InlineData(403, "{}", ServerOutcome.AuthFailed, null)]
        [InlineData(404, "{}", ServerOutcome.ServerError, null)]
        [InlineData(500, "{}", ServerOutcome.ServerError, null)]
        [InlineData(503, "{}", ServerOutcome.ServerError, null)]
        [InlineData(200, "{}", ServerOutcome.ServerError, null)]
        [InlineData(200, @"{""serverTimeUtc"":""2026-11-22T12:00:00.123+09:00""}", ServerOutcome.ServerError, null)]
        [InlineData(200, "{", ServerOutcome.ServerError, null)]
        public async Task Response_ClassifiesStatusAndValidatesFields(int status, string body,
            ServerOutcome expected, string expectedError)
        {
            var handler = new RecordingHandler((_, __) => Task.FromResult(Response(status, body)));
            using (var client = CreateClient(handler))
            {
                var result = await client.SendHeartbeatAsync(RunId, CancellationToken.None);

                Assert.Equal(expected, result.Outcome);
                Assert.Equal(status, result.StatusCode);
                Assert.Equal(expectedError, result.ErrorKind);
                Assert.Null(result.ReceivedAt);
                Assert.Null(result.ServerTimeUtc);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SendFailure_ClassifiesConnectionAndCertificateErrors(bool certificateFailure)
        {
            var handler = new RecordingHandler((_, __) => throw new HttpRequestException(ApiKey,
                certificateFailure ? new AuthenticationException(ApiKey) : null));
            using (var client = CreateClient(handler))
            {
                var result = await client.SendHeartbeatAsync(RunId, CancellationToken.None);

                Assert.Equal(certificateFailure ? ServerOutcome.CertificateFailed : ServerOutcome.ConnectionFailed,
                    result.Outcome);
                Assert.Null(result.StatusCode);
                Assert.Null(result.ErrorKind);
                Assert.Null(result.ReceivedAt);
                Assert.Null(result.ServerTimeUtc);
            }
        }

        [Theory]
        [InlineData(WebExceptionStatus.TrustFailure)]
        [InlineData(WebExceptionStatus.SecureChannelFailure)]
        [InlineData(WebExceptionStatus.ConnectFailure)]
        public async Task WebException_ClassifiesCertificateStatus(WebExceptionStatus status)
        {
            var handler = new RecordingHandler((_, __) => throw new HttpRequestException(ApiKey,
                new WebException(ApiKey, status)));
            using (var client = CreateClient(handler))
            {
                var result = await client.SendHeartbeatAsync(RunId, CancellationToken.None);

                Assert.Equal(status == WebExceptionStatus.ConnectFailure
                    ? ServerOutcome.ConnectionFailed : ServerOutcome.CertificateFailed, result.Outcome);
            }
        }

        [Fact]
        public async Task Timeout_WithoutCallerCancellation_ReturnsTimeout()
        {
            var handler = new RecordingHandler((_, token) => WaitUntilCancelled(token));
            using (var client = CreateClient(handler, requestTimeout: TimeSpan.FromMilliseconds(50)))
            {
                var result = await client.SendHeartbeatAsync(RunId, CancellationToken.None);
                Assert.Equal(ServerOutcome.Timeout, result.Outcome);
                Assert.Null(result.StatusCode);
            }
        }

        [Fact]
        public async Task Cancellation_ByCaller_ThrowsOperationCanceledException()
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new RecordingHandler((_, token) =>
            {
                entered.SetResult(true);
                return WaitUntilCancelled(token);
            });
            using (var cancellation = new CancellationTokenSource())
            using (var client = CreateClient(handler))
            {
                var request = client.SendHeartbeatAsync(RunId, cancellation.Token);
                await entered.Task;
                cancellation.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(26)]
        public async Task SendBatches_RejectInvalidSizeBeforeSending(int count)
        {
            var handler = new RecordingHandler((_, __) => throw new InvalidOperationException());
            using (var client = CreateClient(handler))
            {
                var alarms = new ConfirmedAlarmEvent[count];
                var samples = new ConfirmedSample[count];
                await Assert.ThrowsAsync<ArgumentException>(async () =>
                    await client.SendAlarmEventsAsync(RunId, Noon, alarms, CancellationToken.None));
                await Assert.ThrowsAsync<ArgumentException>(async () =>
                    await client.SendSamplesAsync(RunId, Noon, samples, CancellationToken.None));
                Assert.Null(handler.RequestUri);
            }
        }

        [Theory]
        [InlineData(ApiKey)]
        [InlineData("change_me_collector_http_test_key")]
        public async Task ErrorResponse_DoesNotExposeApiKeyInResult(string key)
        {
            var handler = new RecordingHandler((_, __) => Task.FromResult(Response(400,
                JsonConvert.SerializeObject(new { error = key, detail = key }))));
            using (var client = CreateClient(handler, apiKey: key))
            {
                var result = await client.SendHeartbeatAsync(RunId, CancellationToken.None);

                Assert.Equal(ServerOutcome.BadRequest, result.Outcome);
                Assert.Null(result.ErrorKind);
                Assert.DoesNotContain(key, JsonConvert.SerializeObject(result));
            }
        }

        private static string SuccessBody => "{\"serverTimeUtc\":\"" + ServerTime + "\"}";

        private static HttpServerClient CreateClient(HttpMessageHandler handler, Uri baseUrl = null,
            TimeSpan? requestTimeout = null, Func<TimeSpan> now = null, string apiKey = ApiKey)
        {
            return new HttpServerClient(baseUrl ?? new Uri("https://collector.example.test"), apiKey,
                requestTimeout ?? TimeSpan.FromSeconds(5), now ?? (() => TimeSpan.Zero), handler);
        }

        private static HttpResponseMessage Response(int status, string body)
        {
            return new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        private static async Task<HttpResponseMessage> WaitUntilCancelled(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        }

        private static void AssertRequest(RecordingHandler handler, string endpoint, string expectedBody)
        {
            Assert.Equal(HttpMethod.Post, handler.Method);
            Assert.Equal("https://collector.example.test/prefix/api/v1/collector/" + endpoint,
                handler.RequestUri.AbsoluteUri);
            Assert.Equal(ApiKey, Assert.Single(handler.ApiKeys));
            Assert.Equal("application/json; charset=utf-8", handler.ContentType);
            var settings = new JsonSerializerSettings { DateParseHandling = DateParseHandling.None };
            var expected = JsonConvert.DeserializeObject<JObject>(expectedBody, settings);
            var actual = JsonConvert.DeserializeObject<JObject>(handler.Body, settings);
            Assert.True(JToken.DeepEquals(expected, actual), handler.Body);
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;

            public Uri RequestUri { get; private set; }
            public HttpMethod Method { get; private set; }
            public string[] ApiKeys { get; private set; }
            public string ContentType { get; private set; }
            public string Body { get; private set; }

            public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
            {
                this.respond = respond;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                RequestUri = request.RequestUri;
                Method = request.Method;
                ApiKeys = request.Headers.GetValues("X-Api-Key").ToArray();
                ContentType = request.Content.Headers.ContentType.ToString();
                Body = await request.Content.ReadAsStringAsync();
                return await respond(request, cancellationToken);
            }
        }

        private sealed class ObservedContent : StringContent
        {
            private readonly Action onBuffered;

            public ObservedContent(string body, Action onBuffered) : base(body, Encoding.UTF8, "application/json")
            {
                this.onBuffered = onBuffered;
            }

            protected override async Task SerializeToStreamAsync(System.IO.Stream stream, TransportContext context)
            {
                await base.SerializeToStreamAsync(stream, context);
                onBuffered();
            }
        }
    }
}
