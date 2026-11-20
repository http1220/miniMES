using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using Core;
using Data;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Server
{
    public sealed class FaultsController : ApiController
    {
        private const string Endpoint = "/api/v1/faults/{faultId}/transitions";
        private readonly ApiRuntime runtime;
        public FaultsController(ApiRuntime runtime) { this.runtime = runtime; }

        [HttpPost, Route("api/v1/faults/{faultId}/transitions"), DashboardKey]
        public async Task<HttpResponseMessage> Transition(string faultId)
        {
            int? parsedId = null, userId = null;
            if (Request.Content != null && Request.Content.Headers.ContentLength > 65536)
                return Failure(HttpStatusCode.BadRequest, "invalid_field", parsedId, userId);
            JToken body;
            try
            {
                if (Request.Content == null) return Failure(HttpStatusCode.BadRequest, "invalid_json", parsedId, userId);
                var text = await Request.Content.ReadAsStringAsync().ConfigureAwait(false);
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                {
                    body = JToken.ReadFrom(reader);
                    if (reader.Read()) return Failure(HttpStatusCode.BadRequest, "invalid_json", parsedId, userId);
                }
            }
            catch (JsonException exception)
            {
                return Failure(HttpStatusCode.BadRequest, "invalid_json", parsedId, userId, exception);
            }
            int id;
            if (!int.TryParse(faultId, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id < 1)
                return Failure(HttpStatusCode.BadRequest, "invalid_field", parsedId, userId);
            parsedId = id;
            var obj = body as JObject;
            if (obj == null) return Failure(HttpStatusCode.BadRequest, "invalid_field", parsedId, userId);
            long value;
            var token = obj["userId"];
            if (token != null && token.Type != JTokenType.Null)
            {
                if (!CollectorInput.TryInteger(token, int.MinValue, int.MaxValue, out value))
                    return Failure(HttpStatusCode.BadRequest, "invalid_field", parsedId, userId);
                userId = (int)value;
            }
            FaultStatus expected, next;
            if (!StatusNames.TryRead(obj["expectedStatus"], out expected) || !StatusNames.TryRead(obj["newStatus"], out next))
                return Failure(HttpStatusCode.BadRequest, "invalid_field", parsedId, userId);
            string reason = null;
            token = obj["reason"];
            if (token != null && token.Type != JTokenType.Null)
            {
                if (token.Type != JTokenType.String || ((string)token).Length > 1000)
                    return Failure(HttpStatusCode.BadRequest, "invalid_field", parsedId, userId);
                reason = (string)token;
            }
            DateTime? recoveredAt = null;
            token = obj["recoveredAtUtc"];
            if (token != null && token.Type != JTokenType.Null)
            {
                DateTime parsed;
                if (!CollectorInput.TryUtc(token, out parsed))
                    return Failure(HttpStatusCode.BadRequest, "invalid_field", parsedId, userId);
                recoveredAt = parsed;
            }
            if (!userId.HasValue || userId.Value <= 0)
                return Failure(HttpStatusCode.BadRequest, "user_required", parsedId, userId);
            var now = runtime.UtcNow();
            now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
            try
            {
                using (var connection = new SqlConnection(runtime.Settings.ConnectionString))
                {
                    connection.Open();
                    var repository = new FaultRepository(connection);
                    var context = repository.GetTransitionContext(id);
                    if (context == null) return Failure(HttpStatusCode.NotFound, "not_found", parsedId, userId);
                    var currentName = StatusNames.Format((FaultStatus)context.Status);
                    if (context.Status != (byte)expected) return Mismatch(currentName, parsedId, userId);
                    var check = FaultStateRules.ValidateTransition(expected, next, reason, recoveredAt,
                        DateTime.SpecifyKind(context.AlarmRaisedAtUtc, DateTimeKind.Utc), now);
                    if (check == FaultTransitionCheckResult.InvalidTransition)
                        return Failure(HttpStatusCode.BadRequest, "invalid_transition", parsedId, userId);
                    if (check == FaultTransitionCheckResult.ReasonRequired)
                        return Failure(HttpStatusCode.BadRequest, "reason_required", parsedId, userId);
                    if (check == FaultTransitionCheckResult.InvalidRecoveryTime)
                        return Failure(HttpStatusCode.BadRequest, "invalid_recovery_time", parsedId, userId);
                    if (check != FaultTransitionCheckResult.Ok) throw new InvalidOperationException();
                    var result = repository.TransitionFault(id, (byte)expected, (byte)next, userId.Value, now, reason, recoveredAt);
                    if (result.Status == FaultTransitionStatus.NotFound)
                        return Failure(HttpStatusCode.NotFound, "not_found", parsedId, userId);
                    if (result.Status == FaultTransitionStatus.StatusMismatch)
                        return Mismatch(StatusNames.Format((FaultStatus)result.CurrentStatus), parsedId, userId);
                    if (result.Status == FaultTransitionStatus.ActiveFaultExists)
                    {
                        LogFailure(HttpStatusCode.Conflict, "already_registered", parsedId, userId, null, null);
                        return Request.CreateResponse(HttpStatusCode.Conflict, new { error = "already_registered", faultId = result.FaultId });
                    }
                    if (result.Status != FaultTransitionStatus.Success) throw new InvalidOperationException();
                    runtime.Logger.Information("Dashboard success {Endpoint} {FaultId} {UserId}", Endpoint, id, userId);
                    return Request.CreateResponse(HttpStatusCode.OK,
                        new { serverTimeUtc = now, status = StatusNames.Format(next), version = Convert.ToBase64String(result.Version) });
                }
            }
            catch (SqlException exception)
            {
                var status = CollectorFailure.ClassifySql(exception.Number);
                if (status == HttpStatusCode.ServiceUnavailable) runtime.Database.MarkDisconnected();
                return Failure(status, status == HttpStatusCode.BadRequest ? "invalid_field" : "database_unavailable",
                    parsedId, userId, exception, exception.Number);
            }
            catch (Exception exception)
            {
                return Failure(CollectorFailure.ClassifyNonSql(exception), "internal", parsedId, userId, exception);
            }
        }

        private HttpResponseMessage Mismatch(string currentStatus, int? id, int? userId)
        {
            LogFailure(HttpStatusCode.Conflict, "status_mismatch", id, userId, null, null);
            return Request.CreateResponse(HttpStatusCode.Conflict, new { error = "status_mismatch", currentStatus });
        }

        private HttpResponseMessage Failure(HttpStatusCode status, string error, int? id, int? userId,
            Exception exception = null, int? sqlNumber = null)
        {
            LogFailure(status, error, id, userId, exception, sqlNumber);
            return ApiConfiguration.Error(Request, status, error);
        }

        private void LogFailure(HttpStatusCode status, string error, int? id, int? userId, Exception exception, int? sqlNumber)
        {
            var level = (int)status < 500 ? Serilog.Events.LogEventLevel.Warning : Serilog.Events.LogEventLevel.Error;
            runtime.Logger.Write(level, "Dashboard failure {Endpoint} {Error} {FaultId} {UserId} {ExceptionType} {SqlNumber}",
                Endpoint, error, id, userId, exception?.GetType().FullName, sqlNumber);
        }

        private static class StatusNames
        {
            private static readonly string[] Names = { null, "registered", "in_progress", "closed", "cancelled" };
            public static bool TryRead(JToken token, out FaultStatus status)
            {
                status = default(FaultStatus);
                if (token == null || token.Type != JTokenType.String) return false;
                for (var i = 1; i < Names.Length; i++)
                    if ((string)token == Names[i]) { status = (FaultStatus)i; return true; }
                return false;
            }
            public static string Format(FaultStatus status)
            {
                var index = (int)status;
                if (index < 1 || index >= Names.Length) throw new InvalidOperationException();
                return Names[index];
            }
        }
    }
}
