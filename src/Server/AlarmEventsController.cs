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
    public sealed class AlarmEventsController : ApiController
    {
        private const string Endpoint = "/api/v1/collector/alarm-events";
        private readonly ApiRuntime runtime;

        public AlarmEventsController(ApiRuntime runtime) { this.runtime = runtime; }

        [HttpPost, Route("api/v1/collector/alarm-events"), CollectorKey]
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
            AlarmEventsInput input;
            var error = AlarmEventsInput.Validate(body as JObject, out input);
            if (error != null) return Failure(HttpStatusCode.BadRequest, error);
            AlarmEventBatchResult result;
            try
            {
                using (var connection = new SqlConnection(runtime.Settings.ConnectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        new CollectorRunRepository(connection).EnsureRun(input.RunId, input.StartedAtUtc, transaction);
                        result = new AlarmRepository(connection).ApplyEvents(input.Events, transaction);
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
            foreach (var mismatch in result.Mismatches)
                runtime.Logger.Warning("Alarm event mismatch {EquipmentId} {RunId} {EventSequence} {Cause} {EventType}",
                    mismatch.EquipmentId, mismatch.RunId, mismatch.EventSequence, mismatch.Cause, mismatch.EventType);
            runtime.Logger.Information("Collector success {Endpoint} {RunId} {MinSequence} {MaxSequence} {AppliedEvents} {DuplicateEvents}",
                Endpoint, input.RunId, input.Events.Min(e => e.EventSequence), input.Events.Max(e => e.EventSequence),
                result.AppliedCount, result.DuplicateCount);
            return Request.CreateResponse(HttpStatusCode.OK, new { serverTimeUtc = runtime.UtcNow(),
                appliedEvents = result.AppliedCount, duplicateEvents = result.DuplicateCount });
        }

        private HttpResponseMessage Failure(HttpStatusCode status, string error, AlarmEventsInput input = null,
            Exception exception = null, int? sqlNumber = null)
        {
            var level = status == HttpStatusCode.BadRequest ? Serilog.Events.LogEventLevel.Warning : Serilog.Events.LogEventLevel.Error;
            runtime.Logger.Write(level, "Collector failure {Endpoint} {Error} {RunId} {MinSequence} {MaxSequence} {ExceptionType} {SqlNumber}",
                Endpoint, error, input == null ? (Guid?)null : input.RunId,
                input == null ? (long?)null : input.Events.Min(e => e.EventSequence),
                input == null ? (long?)null : input.Events.Max(e => e.EventSequence), exception?.GetType().FullName, sqlNumber);
            return ApiConfiguration.Error(Request, status, error);
        }
    }
}
