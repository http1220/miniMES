using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using Data;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Server
{
    public sealed class SamplesController : ApiController
    {
        private const string Endpoint = "/api/v1/collector/samples";
        private readonly ApiRuntime runtime;

        public SamplesController(ApiRuntime runtime) { this.runtime = runtime; }

        [HttpPost, Route("api/v1/collector/samples"), CollectorKey]
        public async Task<HttpResponseMessage> Post()
        {
            if (Request.Content != null && Request.Content.Headers.ContentLength > 1048576)
                return Failure(HttpStatusCode.BadRequest, "batch_too_large");
            JToken body;
            try
            {
                if (Request.Content == null) return Failure(HttpStatusCode.BadRequest, "invalid_json");
                var text = await Request.Content.ReadAsStringAsync().ConfigureAwait(false);
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                {
                    body = JToken.ReadFrom(reader);
                    if (reader.Read()) return Failure(HttpStatusCode.BadRequest, "invalid_json");
                }
            }
            catch (JsonException exception)
            {
                return Failure(HttpStatusCode.BadRequest, "invalid_json", exception: exception);
            }
            SamplesInput input;
            var error = SamplesInput.Validate(body as JObject, out input);
            if (error != null) return Failure(HttpStatusCode.BadRequest, error);
            SampleBatchResult result;
            DateTime recordedAtUtc;
            try
            {
                recordedAtUtc = runtime.UtcNow();
                using (var connection = new SqlConnection(runtime.Settings.ConnectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        new CollectorRunRepository(connection).EnsureRun(input.RunId, input.StartedAtUtc, transaction);
                        result = new SampleRepository(connection).SaveSamples(input.Samples, recordedAtUtc, transaction);
                        transaction.Commit();
                    }
                }
            }
            catch (SqlException exception)
            {
                var status = CollectorFailure.ClassifySql(exception.Number);
                if (status == HttpStatusCode.ServiceUnavailable) runtime.Database.MarkDisconnected();
                return Failure(status, status == HttpStatusCode.BadRequest ? "invalid_field" : "database_unavailable",
                    input, exception, exception.Number);
            }
            catch (Exception exception)
            {
                return Failure(CollectorFailure.ClassifyNonSql(exception), "internal", input, exception);
            }
            runtime.Logger.Information("Collector success {Endpoint} {RunId} {MinSequence} {MaxSequence} {StoredSamples} {DuplicateSamples}",
                Endpoint, input.RunId, input.Samples.Min(e => e.SequenceNumber), input.Samples.Max(e => e.SequenceNumber),
                result.StoredCount, result.DuplicateCount);
            return Request.CreateResponse(HttpStatusCode.OK, new { serverTimeUtc = recordedAtUtc,
                storedSamples = result.StoredCount, duplicateSamples = result.DuplicateCount });
        }

        private HttpResponseMessage Failure(HttpStatusCode status, string error, SamplesInput input = null,
            Exception exception = null, int? sqlNumber = null)
        {
            var level = status == HttpStatusCode.BadRequest ? Serilog.Events.LogEventLevel.Warning : Serilog.Events.LogEventLevel.Error;
            runtime.Logger.Write(level, "Collector failure {Endpoint} {Error} {RunId} {MinSequence} {MaxSequence} {ExceptionType} {SqlNumber}",
                Endpoint, error, input == null ? (Guid?)null : input.RunId,
                input == null ? (long?)null : input.Samples.Min(e => e.SequenceNumber),
                input == null ? (long?)null : input.Samples.Max(e => e.SequenceNumber), exception?.GetType().FullName, sqlNumber);
            return ApiConfiguration.Error(Request, status, error);
        }
    }
}
