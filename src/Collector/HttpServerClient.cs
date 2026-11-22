using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Collector
{
    public sealed class HttpServerClient : IServerClient, IDisposable
    {
        private const int MaxBatchSize = 25;
        private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        private const string ApiPath = "api/v1/collector/";
        private static readonly Regex ErrorKindPattern = new Regex(@"\A[a-z_]{1,40}\z");
        private static readonly string[] ServerTimeFormats =
        {
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:ss'+00:00'",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'+00:00'"
        };

        private readonly HttpClient client;
        private readonly Func<TimeSpan> now;
        private readonly string apiKey;

        static HttpServerClient()
        {
            ServicePointManager.DefaultConnectionLimit = 5;
        }

        public HttpServerClient(Uri baseUrl, string apiKey, TimeSpan requestTimeout, Func<TimeSpan> now,
            HttpMessageHandler handler = null)
        {
            if (baseUrl == null || !baseUrl.IsAbsoluteUri || baseUrl.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("Server base URL must be absolute HTTPS.", nameof(baseUrl));
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("API key is required.", nameof(apiKey));
            if (apiKey.Contains("\r") || apiKey.Contains("\n"))
                throw new ArgumentException("API key is invalid as an HTTP header.", nameof(apiKey));

            this.now = now ?? throw new ArgumentNullException(nameof(now));
            this.apiKey = apiKey;
            var baseAddress = new Uri(baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");

            client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
            try
            {
                client.BaseAddress = baseAddress;
                client.Timeout = requestTimeout;
                client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
            }
            catch (FormatException)
            {
                client.Dispose();
                throw new ArgumentException("API key is invalid as an HTTP header.", nameof(apiKey));
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public Task<ServerResult> SendAlarmEventsAsync(Guid runId, DateTime startedAtUtc,
            IReadOnlyList<ConfirmedAlarmEvent> events, CancellationToken cancellationToken)
        {
            ValidateBatch(events, nameof(events));
            return SendAsync("alarm-events", new
            {
                runId,
                startedAtUtc = FormatUtc(startedAtUtc),
                events = events.Select(alarm => new
                {
                    eventSequence = alarm.Item.EventSequence,
                    equipmentId = alarm.Item.EquipmentId,
                    cause = (int)alarm.Item.Cause,
                    eventType = (int)alarm.Item.EventType,
                    occurredAtUtc = FormatUtc(alarm.OccurredAtUtc)
                })
            }, cancellationToken);
        }

        public Task<ServerResult> SendSamplesAsync(Guid runId, DateTime startedAtUtc,
            IReadOnlyList<ConfirmedSample> samples, CancellationToken cancellationToken)
        {
            ValidateBatch(samples, nameof(samples));
            return SendAsync("samples", new
            {
                runId,
                startedAtUtc = FormatUtc(startedAtUtc),
                samples = samples.Select(sample => new
                {
                    equipmentId = sample.Item.EquipmentId,
                    sequenceNumber = sample.Item.SequenceNumber,
                    collectedAtUtc = FormatUtc(sample.CollectedAtUtc),
                    temperature = sample.Item.Temperature,
                    vibration = sample.Item.Vibration,
                    statusCode = sample.Item.StatusCode
                })
            }, cancellationToken);
        }

        public Task<ServerResult> SendHeartbeatAsync(Guid runId, CancellationToken cancellationToken)
        {
            return SendAsync("heartbeat", new { runId }, cancellationToken);
        }

        private static void ValidateBatch<T>(IReadOnlyList<T> items, string parameterName)
        {
            if (items == null)
                throw new ArgumentNullException(parameterName);
            if (items.Count == 0 || items.Count > MaxBatchSize)
                throw new ArgumentException("Batch must contain 1 to 25 items.", parameterName);
        }

        private static string FormatUtc(DateTime value)
        {
            return value.ToUniversalTime().ToString(UtcFormat, CultureInfo.InvariantCulture);
        }

        private async Task<ServerResult> SendAsync(string endpoint, object payload, CancellationToken cancellationToken)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, ApiPath + endpoint))
            {
                request.Content = new StringContent(JsonConvert.SerializeObject(payload, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Include
                }), Encoding.UTF8, "application/json");

                HttpResponseMessage response;
                try
                {
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (TaskCanceledException)
                {
                    return new ServerResult(ServerOutcome.Timeout);
                }
                catch (Exception error) when (error is HttpRequestException || error is WebException
                    || error is AuthenticationException)
                {
                    return new ServerResult(IsCertificateFailure(error)
                        ? ServerOutcome.CertificateFailed
                        : ServerOutcome.ConnectionFailed);
                }

                using (response)
                {
                    var receivedAt = now();
                    var status = (int)response.StatusCode;
                    if (response.IsSuccessStatusCode)
                    {
                        var serverTime = await ReadStringFieldAsync(response.Content, "serverTimeUtc").ConfigureAwait(false);
                        if (DateTime.TryParseExact(serverTime, ServerTimeFormats, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var serverUtc))
                            return new ServerResult(ServerOutcome.Success, receivedAt, serverUtc, status);
                    }
                    else if (response.StatusCode == HttpStatusCode.BadRequest)
                    {
                        var errorKind = await ReadStringFieldAsync(response.Content, "error").ConfigureAwait(false);
                        if (errorKind != null && (!ErrorKindPattern.IsMatch(errorKind)
                            || errorKind.IndexOf(apiKey, StringComparison.Ordinal) >= 0))
                            errorKind = null;
                        return new ServerResult(ServerOutcome.BadRequest, statusCode: status, errorKind: errorKind);
                    }
                    else if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        return new ServerResult(ServerOutcome.AuthFailed, statusCode: status);
                    }

                    return new ServerResult(ServerOutcome.ServerError, statusCode: status);
                }
            }
        }

        private static async Task<string> ReadStringFieldAsync(HttpContent content, string field)
        {
            if (content == null)
                return null;

            try
            {
                var body = await content.ReadAsStringAsync().ConfigureAwait(false);
                using (var reader = new JsonTextReader(new StringReader(body)) { DateParseHandling = DateParseHandling.None })
                {
                    var document = JObject.Load(reader);
                    var value = document[field];
                    if (reader.Read() || value?.Type != JTokenType.String)
                        return null;
                    return (string)value;
                }
            }
            catch (JsonException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                // HttpContent can reject the declared response charset.
                return null;
            }
        }

        private static bool IsCertificateFailure(Exception error)
        {
            for (var current = error; current != null; current = current.InnerException)
            {
                if (current is AuthenticationException ||
                    current is WebException web && (web.Status == WebExceptionStatus.TrustFailure
                        || web.Status == WebExceptionStatus.SecureChannelFailure))
                    return true;
            }
            return false;
        }

        public void Dispose()
        {
            client.Dispose();
        }
    }
}
