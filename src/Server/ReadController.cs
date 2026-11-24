using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using Core;
using Data;
using Microsoft.Data.SqlClient;

namespace Server
{
    public sealed class ReadController : ApiController
    {
        private const string EquipmentEndpoint = "/api/v1/equipment/latest";
        private const string AlarmsEndpoint = "/api/v1/alarms";
        private const string UsersEndpoint = "/api/v1/users";
        private const string FaultsEndpoint = "/api/v1/faults";
        private const string FaultDetailEndpoint = "/api/v1/faults/{faultId}";

        // Keep the wire names in sync with FaultsController.StatusNames and contract §3.1.
        private static readonly string[] FaultStatusNames = { null, "registered", "in_progress", "closed", "cancelled" };
        private readonly ApiRuntime runtime;

        public ReadController(ApiRuntime runtime) { this.runtime = runtime; }

        [HttpGet, Route("api/v1/equipment/latest"), DashboardKey]
        public HttpResponseMessage LatestEquipment()
        {
            return Read(EquipmentEndpoint, repository => OkResponse(new
            {
                serverTimeUtc = ServerTimeUtc(),
                equipment = repository.GetLatestEquipment().Select(row => new
                {
                    row.EquipmentId,
                    row.Name,
                    connection = row.HasActiveCommunicationAlarm ? "lost" : "connected",
                    statusCode = row.StatusCode <= 2 ? row.StatusCode : null,
                    row.Temperature,
                    row.Vibration,
                    row.LastSampleAtUtc
                }).ToArray()
            }));
        }

        [HttpGet, Route("api/v1/alarms"), DashboardKey]
        public HttpResponseMessage ActiveAlarms()
        {
            if (!HasActiveQuery())
                return Failure(AlarmsEndpoint, HttpStatusCode.BadRequest, "invalid_field");

            return Read(AlarmsEndpoint, repository => OkResponse(new
            {
                serverTimeUtc = ServerTimeUtc(),
                alarms = repository.GetActiveAlarms().Select(row => new
                {
                    row.AlarmId,
                    row.EquipmentId,
                    row.Cause,
                    row.RaisedAtUtc,
                    row.AcknowledgedAtUtc,
                    row.AcknowledgedByUserId,
                    row.AcknowledgedByName,
                    row.FaultId
                }).ToArray()
            }));
        }

        [HttpGet, Route("api/v1/users"), DashboardKey]
        public HttpResponseMessage ActiveUsers()
        {
            if (!HasActiveQuery())
                return Failure(UsersEndpoint, HttpStatusCode.BadRequest, "invalid_field");

            return Read(UsersEndpoint, repository => OkResponse(new
            {
                serverTimeUtc = ServerTimeUtc(),
                users = repository.GetActiveUsers().Select(row => new { row.UserId, row.Name }).ToArray()
            }));
        }

        [HttpGet, Route("api/v1/faults"), DashboardKey]
        public HttpResponseMessage Faults()
        {
            FaultStatus? status = null;
            var values = QueryValues("status");
            if (values.Length != 0)
            {
                var index = Array.IndexOf(FaultStatusNames, values[0], 1);
                if (values.Length != 1 || index < 1)
                    return Failure(FaultsEndpoint, HttpStatusCode.BadRequest, "invalid_field");
                status = (FaultStatus)index;
            }

            return Read(FaultsEndpoint, repository => OkResponse(new
            {
                serverTimeUtc = ServerTimeUtc(),
                faults = repository.ListFaults(status, 200).Select(row => new
                {
                    row.FaultId,
                    row.AlarmId,
                    row.EquipmentId,
                    status = FaultStatusNames[(int)row.Status],
                    row.AlarmRaisedAtUtc,
                    row.RegisteredAtUtc,
                    row.CauseCategory,
                    completedAtUtc = row.RecoveredAtUtc,
                    version = Convert.ToBase64String(row.Version)
                }).ToArray()
            }));
        }

        [HttpGet, Route("api/v1/faults/{faultId}"), DashboardKey]
        public HttpResponseMessage FaultDetail(string faultId)
        {
            int id;
            if (!int.TryParse(faultId, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id < 1)
                return Failure(FaultDetailEndpoint, HttpStatusCode.BadRequest, "invalid_field");

            return Read(FaultDetailEndpoint, repository =>
            {
                var row = repository.GetFaultDetail(id);
                if (row == null)
                    return Failure(FaultDetailEndpoint, HttpStatusCode.NotFound, "not_found");

                return OkResponse(new
                {
                    serverTimeUtc = ServerTimeUtc(),
                    row.FaultId,
                    row.AlarmId,
                    row.EquipmentId,
                    status = FaultStatusNames[(int)row.Status],
                    row.AlarmRaisedAtUtc,
                    row.RegisteredAtUtc,
                    row.CauseCategory,
                    completedAtUtc = row.RecoveredAtUtc,
                    version = Convert.ToBase64String(row.Version),
                    row.CauseText,
                    row.ActionText,
                    row.CancelReason,
                    history = row.History.Select(change => new
                    {
                        change.ChangedAtUtc,
                        change.ChangedByName,
                        previousStatus = FaultStatusNames[(int)change.PreviousStatus],
                        newStatus = FaultStatusNames[(int)change.NewStatus],
                        change.Reason
                    }).ToArray()
                });
            });
        }

        private string[] QueryValues(string name)
        {
            return Request.GetQueryNameValuePairs()
                .Where(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Value).ToArray();
        }

        private bool HasActiveQuery()
        {
            var values = QueryValues("active");
            return values.Length == 1 && string.Equals(values[0], "true", StringComparison.OrdinalIgnoreCase);
        }

        private DateTime ServerTimeUtc()
        {
            var now = runtime.UtcNow();
            return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        }

        private HttpResponseMessage OkResponse<T>(T payload)
        {
            return Request.CreateResponse(HttpStatusCode.OK, payload);
        }

        private HttpResponseMessage Read(string endpoint, Func<DashboardReadRepository, HttpResponseMessage> read)
        {
            try
            {
                using (var connection = new SqlConnection(runtime.Settings.ConnectionString))
                {
                    connection.Open();
                    return read(new DashboardReadRepository(connection));
                }
            }
            catch (SqlException exception)
            {
                var status = CollectorFailure.ClassifySql(exception.Number);
                if (status == HttpStatusCode.ServiceUnavailable) runtime.Database.MarkDisconnected();
                return Failure(endpoint, status, status == HttpStatusCode.BadRequest ? "invalid_field" : "database_unavailable",
                    exception, exception.Number);
            }
            catch (Exception exception)
            {
                return Failure(endpoint, CollectorFailure.ClassifyNonSql(exception), "internal", exception);
            }
        }

        private HttpResponseMessage Failure(string endpoint, HttpStatusCode status, string error,
            Exception exception = null, int? sqlNumber = null)
        {
            var level = (int)status < 500 ? Serilog.Events.LogEventLevel.Warning : Serilog.Events.LogEventLevel.Error;
            runtime.Logger.Write(level, "Dashboard failure {Endpoint} {Error} {ExceptionType} {SqlNumber}",
                endpoint, error, exception?.GetType().FullName, sqlNumber);
            return ApiConfiguration.Error(Request, status, error);
        }
    }
}
