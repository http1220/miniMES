using System;
using System.Globalization;
using System.IO;
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
    public sealed class AlarmsController : ApiController
    {
        private const string Endpoint = "/api/v1/alarms/{alarmId}/acknowledge";
        private readonly ApiRuntime runtime;

        public AlarmsController(ApiRuntime runtime) { this.runtime = runtime; }

        [HttpPost, Route("api/v1/alarms/{alarmId}/acknowledge"), DashboardKey]
        public async Task<HttpResponseMessage> Acknowledge(string alarmId)
        {
            int? parsedAlarmId = null;
            int? userId = null;
            if (Request.Content != null && Request.Content.Headers.ContentLength > 65536)
                return Failure(HttpStatusCode.BadRequest, "invalid_field", parsedAlarmId, userId);
            JToken body;
            try
            {
                if (Request.Content == null) return Failure(HttpStatusCode.BadRequest, "invalid_json", parsedAlarmId, userId);
                var text = await Request.Content.ReadAsStringAsync().ConfigureAwait(false);
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                {
                    body = JToken.ReadFrom(reader);
                    if (reader.Read()) return Failure(HttpStatusCode.BadRequest, "invalid_json", parsedAlarmId, userId);
                }
            }
            catch (JsonException exception)
            {
                return Failure(HttpStatusCode.BadRequest, "invalid_json", parsedAlarmId, userId, exception);
            }
            int id;
            if (!int.TryParse(alarmId, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id < 1)
                return Failure(HttpStatusCode.BadRequest, "invalid_field", parsedAlarmId, userId);
            parsedAlarmId = id;
            var obj = body as JObject;
            if (obj == null) return Failure(HttpStatusCode.BadRequest, "invalid_field", parsedAlarmId, userId);
            var token = obj["userId"];
            long value;
            if (token != null && token.Type != JTokenType.Null)
            {
                if (!CollectorInput.TryInteger(token, int.MinValue, int.MaxValue, out value))
                    return Failure(HttpStatusCode.BadRequest, "invalid_field", parsedAlarmId, userId);
                userId = (int)value;
            }
            if (!userId.HasValue || userId.Value <= 0)
                return Failure(HttpStatusCode.BadRequest, "user_required", parsedAlarmId, userId);
            var now = runtime.UtcNow();
            now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
            AlarmAcknowledgementResult result;
            try
            {
                using (var connection = new SqlConnection(runtime.Settings.ConnectionString))
                {
                    connection.Open();
                    result = new AlarmRepository(connection).AcknowledgeAlarm(id, userId.Value, now);
                }
            }
            catch (SqlException exception)
            {
                var status = CollectorFailure.ClassifySql(exception.Number);
                if (status == HttpStatusCode.ServiceUnavailable) runtime.Database.MarkDisconnected();
                return Failure(status, status == HttpStatusCode.BadRequest ? "invalid_field" : "database_unavailable",
                    parsedAlarmId, userId, exception, exception.Number);
            }
            catch (Exception exception)
            {
                return Failure(CollectorFailure.ClassifyNonSql(exception), "internal", parsedAlarmId, userId, exception);
            }
            if (result.Status == AlarmAcknowledgementStatus.NotFound)
                return Failure(HttpStatusCode.NotFound, "not_found", parsedAlarmId, userId);
            if (result.Status == AlarmAcknowledgementStatus.AlreadyAcknowledged)
            {
                LogFailure(HttpStatusCode.Conflict, "already_acknowledged", parsedAlarmId, userId, null, null);
                return Request.CreateResponse(HttpStatusCode.Conflict,
                    new { error = "already_acknowledged", acknowledgedBy = result.AcknowledgedBy });
            }
            runtime.Logger.Information("Dashboard success {Endpoint} {AlarmId} {UserId}", Endpoint, id, userId);
            return Request.CreateResponse(HttpStatusCode.OK,
                new { serverTimeUtc = now, acknowledgedBy = result.AcknowledgedBy, acknowledgedAtUtc = now });
        }

        private const string FaultEndpoint = "/api/v1/alarms/{alarmId}/fault";

        [HttpPost, Route("api/v1/alarms/{alarmId}/fault"), DashboardKey]
        public async Task<HttpResponseMessage> RegisterFault(string alarmId)
        {
            int? parsedAlarmId = null;
            if (Request.Content != null && Request.Content.Headers.ContentLength > 65536)
                return FaultFailure(HttpStatusCode.BadRequest, "invalid_field", parsedAlarmId);
            JToken body;
            try
            {
                if (Request.Content == null) return FaultFailure(HttpStatusCode.BadRequest, "invalid_json", parsedAlarmId);
                var text = await Request.Content.ReadAsStringAsync().ConfigureAwait(false);
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                {
                    body = JToken.ReadFrom(reader);
                    if (reader.Read()) return FaultFailure(HttpStatusCode.BadRequest, "invalid_json", parsedAlarmId);
                }
            }
            catch (JsonException exception)
            {
                return FaultFailure(HttpStatusCode.BadRequest, "invalid_json", parsedAlarmId, exception);
            }
            int id;
            if (!int.TryParse(alarmId, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id < 1)
                return FaultFailure(HttpStatusCode.BadRequest, "invalid_field", parsedAlarmId);
            parsedAlarmId = id;
            var obj = body as JObject;
            long category;
            string causeText, actionText;
            if (obj == null || !CollectorInput.TryInteger(obj["causeCategory"], 0, 255, out category)
                || !TryFaultText(obj["causeText"], out causeText) || !TryFaultText(obj["actionText"], out actionText))
                return FaultFailure(HttpStatusCode.BadRequest, "invalid_field", parsedAlarmId);
            var now = runtime.UtcNow();
            now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
            FaultRegisterResult result;
            try
            {
                using (var connection = new SqlConnection(runtime.Settings.ConnectionString))
                {
                    connection.Open();
                    result = new FaultRepository(connection).RegisterFault(id, (byte)category, causeText, actionText, now);
                }
            }
            catch (SqlException exception)
            {
                var status = CollectorFailure.ClassifySql(exception.Number);
                if (status == HttpStatusCode.ServiceUnavailable) runtime.Database.MarkDisconnected();
                return FaultFailure(status, status == HttpStatusCode.BadRequest ? "invalid_field" : "database_unavailable",
                    parsedAlarmId, exception, exception.Number);
            }
            catch (Exception exception)
            {
                return FaultFailure(CollectorFailure.ClassifyNonSql(exception), "internal", parsedAlarmId, exception);
            }
            if (result.Status == FaultRegisterStatus.AlarmNotFound)
                return FaultFailure(HttpStatusCode.NotFound, "not_found", parsedAlarmId);
            if (result.Status == FaultRegisterStatus.InvalidCauseCategory)
                return FaultFailure(HttpStatusCode.BadRequest, "invalid_cause_category", parsedAlarmId);
            if (result.Status == FaultRegisterStatus.AlreadyRegistered)
            {
                LogFaultFailure(HttpStatusCode.Conflict, "already_registered", parsedAlarmId, null, null);
                return Request.CreateResponse(HttpStatusCode.Conflict, new { error = "already_registered", faultId = result.FaultId });
            }
            runtime.Logger.Information("Dashboard success {Endpoint} {AlarmId} {FaultId}", FaultEndpoint, id, result.FaultId);
            return Request.CreateResponse(HttpStatusCode.Created,
                new { serverTimeUtc = now, faultId = result.FaultId, status = "registered", version = Convert.ToBase64String(result.Version) });
        }

        private static bool TryFaultText(JToken token, out string value)
        {
            value = null;
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token.Type != JTokenType.String) return false;
            value = (string)token;
            return value.Length <= 2000;
        }

        private HttpResponseMessage FaultFailure(HttpStatusCode status, string error, int? alarmId,
            Exception exception = null, int? sqlNumber = null)
        {
            LogFaultFailure(status, error, alarmId, exception, sqlNumber);
            return ApiConfiguration.Error(Request, status, error);
        }

        private void LogFaultFailure(HttpStatusCode status, string error, int? alarmId, Exception exception, int? sqlNumber)
        {
            var level = (int)status < 500 ? Serilog.Events.LogEventLevel.Warning : Serilog.Events.LogEventLevel.Error;
            runtime.Logger.Write(level, "Dashboard failure {Endpoint} {Error} {AlarmId} {ExceptionType} {SqlNumber}",
                FaultEndpoint, error, alarmId, exception?.GetType().FullName, sqlNumber);
        }

        private HttpResponseMessage Failure(HttpStatusCode status, string error, int? alarmId, int? userId,
            Exception exception = null, int? sqlNumber = null)
        {
            LogFailure(status, error, alarmId, userId, exception, sqlNumber);
            return ApiConfiguration.Error(Request, status, error);
        }

        private void LogFailure(HttpStatusCode status, string error, int? alarmId, int? userId,
            Exception exception, int? sqlNumber)
        {
            var level = (int)status < 500 ? Serilog.Events.LogEventLevel.Warning : Serilog.Events.LogEventLevel.Error;
            runtime.Logger.Write(level, "Dashboard failure {Endpoint} {Error} {AlarmId} {UserId} {ExceptionType} {SqlNumber}",
                Endpoint, error, alarmId, userId, exception?.GetType().FullName, sqlNumber);
        }
    }
}
