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
    public sealed class FaultTextsController : ApiController
    {
        private const string Endpoint = "/api/v1/faults/{faultId}/texts";
        private readonly ApiRuntime runtime;
        public FaultTextsController(ApiRuntime runtime) { this.runtime = runtime; }

        [HttpPut, Route("api/v1/faults/{faultId}/texts"), DashboardKey]
        public async Task<HttpResponseMessage> UpdateTexts(string faultId)
        {
            int? parsedId = null;
            if (Request.Content != null && Request.Content.Headers.ContentLength > 65536)
                return Failure(HttpStatusCode.BadRequest, "invalid_field", parsedId);
            JToken body;
            try
            {
                if (Request.Content == null) return Failure(HttpStatusCode.BadRequest, "invalid_json", parsedId);
                var text = await Request.Content.ReadAsStringAsync().ConfigureAwait(false);
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                {
                    body = JToken.ReadFrom(reader);
                    if (reader.Read()) return Failure(HttpStatusCode.BadRequest, "invalid_json", parsedId);
                }
            }
            catch (JsonException exception)
            {
                return Failure(HttpStatusCode.BadRequest, "invalid_json", parsedId, exception);
            }
            int id;
            if (!int.TryParse(faultId, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id < 1)
                return Failure(HttpStatusCode.BadRequest, "invalid_field", parsedId);
            parsedId = id;
            var obj = body as JObject;
            byte[] version;
            long category;
            string causeText, actionText;
            if (obj == null || !TryVersion(obj["expectedVersion"], out version) ||
                !CollectorInput.TryInteger(obj["causeCategory"], 0, 255, out category) ||
                !TryText(obj["causeText"], out causeText) || !TryText(obj["actionText"], out actionText))
                return Failure(HttpStatusCode.BadRequest, "invalid_field", parsedId);
            var now = runtime.UtcNow();
            now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
            try
            {
                using (var connection = new SqlConnection(runtime.Settings.ConnectionString))
                {
                    connection.Open();
                    var result = new FaultRepository(connection).UpdateTexts(id, version, (byte)category, causeText, actionText);
                    if (result.Status == FaultTextsUpdateStatus.NotFound)
                        return Failure(HttpStatusCode.NotFound, "not_found", parsedId);
                    if (result.Status == FaultTextsUpdateStatus.InvalidCauseCategory)
                        return Failure(HttpStatusCode.BadRequest, "invalid_cause_category", parsedId);
                    if (result.Status == FaultTextsUpdateStatus.FaultClosed)
                    {
                        LogFailure(HttpStatusCode.Conflict, "fault_closed", parsedId, null, null);
                        return Request.CreateResponse(HttpStatusCode.Conflict, new { error = "fault_closed", status = "closed" });
                    }
                    if (result.Status == FaultTextsUpdateStatus.Conflict)
                    {
                        LogFailure(HttpStatusCode.Conflict, "version_conflict", parsedId, null, null);
                        return Request.CreateResponse(HttpStatusCode.Conflict, new { error = "version_conflict",
                            causeCategory = result.CauseCategory, causeText = result.CauseText,
                            actionText = result.ActionText, version = Convert.ToBase64String(result.Version) });
                    }
                    if (result.Status != FaultTextsUpdateStatus.Success) throw new InvalidOperationException();
                    runtime.Logger.Information("Dashboard success {Endpoint} {FaultId}", Endpoint, id);
                    return Request.CreateResponse(HttpStatusCode.OK, new { serverTimeUtc = now, version = Convert.ToBase64String(result.Version) });
                }
            }
            catch (SqlException exception)
            {
                var status = CollectorFailure.ClassifySql(exception.Number);
                if (status == HttpStatusCode.ServiceUnavailable) runtime.Database.MarkDisconnected();
                return Failure(status, status == HttpStatusCode.BadRequest ? "invalid_field" : "database_unavailable", parsedId, exception, exception.Number);
            }
            catch (Exception exception)
            {
                return Failure(CollectorFailure.ClassifyNonSql(exception), "internal", parsedId, exception);
            }
        }

        private static bool TryVersion(JToken token, out byte[] version)
        {
            version = null;
            if (token == null || token.Type != JTokenType.String) return false;
            var text = (string)token;
            try { version = Convert.FromBase64String(text); }
            catch (FormatException) { return false; }
            return version.Length == 8 && Convert.ToBase64String(version) == text;
        }

        private static bool TryText(JToken token, out string value)
        {
            value = null;
            if (token == null) return false;
            if (token.Type == JTokenType.Null) return true;
            if (token.Type != JTokenType.String) return false;
            value = (string)token;
            return value.Length <= 2000;
        }

        private HttpResponseMessage Failure(HttpStatusCode status, string error, int? id, Exception exception = null, int? sqlNumber = null)
        {
            LogFailure(status, error, id, exception, sqlNumber);
            return ApiConfiguration.Error(Request, status, error);
        }

        private void LogFailure(HttpStatusCode status, string error, int? id, Exception exception, int? sqlNumber)
        {
            var level = (int)status < 500 ? Serilog.Events.LogEventLevel.Warning : Serilog.Events.LogEventLevel.Error;
            runtime.Logger.Write(level, "Dashboard failure {Endpoint} {Error} {FaultId} {ExceptionType} {SqlNumber}",
                Endpoint, error, id, exception?.GetType().FullName, sqlNumber);
        }
    }
}
