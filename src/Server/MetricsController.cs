using System;
using System.Diagnostics;
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
    public sealed class MetricsController : ApiController
    {
        private const string MetricsEndpoint = "/api/v1/metrics";
        private const string AvailabilityEndpoint = "/api/v1/systems/{systemId}/availability";
        private readonly ApiRuntime runtime;

        public MetricsController(ApiRuntime runtime) { this.runtime = runtime; }

        [HttpGet, Route("api/v1/metrics"), DashboardKey]
        public HttpResponseMessage Metrics()
        {
            var timer = Stopwatch.StartNew();
            if (!TryGetPeriod(out var fromUtc, out var toUtc))
                return Failure(MetricsEndpoint, HttpStatusCode.BadRequest, "invalid_field");

            return Read(MetricsEndpoint, (repository, samples) =>
            {
                var equipment = repository.GetEquipmentIds().Select(id =>
                {
                    var result = CalculateEquipment(repository, samples, id, fromUtc, toUtc);
                    return new
                    {
                        equipmentId = id,
                        mtbfSeconds = result.Mtbf.HasValue ? result.Mtbf.Value.TotalSeconds : (double?)null,
                        mttrSeconds = result.Mttr.HasValue ? result.Mttr.Value.TotalSeconds : (double?)null,
                        availability = AvailabilityValue(result.Availability),
                        unavailable = new
                        {
                            mtbf = Unavailable(result.Mtbf),
                            mttr = Unavailable(result.Mttr),
                            availability = Unavailable(result.Availability)
                        },
                        uptimeSeconds = result.UptimeDuration.TotalSeconds,
                        gapSeconds = result.GapDuration.TotalSeconds,
                        observedSeconds = result.ObservedDuration.TotalSeconds,
                        result.FaultCount,
                        result.ClosedFaultCount,
                        result.UnclosedFaultCount
                    };
                }).ToArray();

                return Computed(MetricsEndpoint, toUtc - fromUtc, equipment.Length, timer, new
                {
                    serverTimeUtc = MetricsPeriod.TruncateMilliseconds(runtime.UtcNow()),
                    fromUtc,
                    toUtc,
                    equipment
                });
            });
        }

        [HttpGet, Route("api/v1/systems/{systemId}/availability"), DashboardKey]
        public HttpResponseMessage SystemAvailability(string systemId)
        {
            var timer = Stopwatch.StartNew();
            if (!TryGetPeriod(out var fromUtc, out var toUtc) ||
                !int.TryParse(systemId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1)
                return Failure(AvailabilityEndpoint, HttpStatusCode.BadRequest, "invalid_field");

            return Read(AvailabilityEndpoint, (repository, samples) =>
            {
                var system = repository.GetSystem(id);
                if (system == null)
                    return Failure(AvailabilityEndpoint, HttpStatusCode.NotFound, "not_found");

                var nodes = repository.GetSystemNodes().Select(row => new SystemNodeInput(row.NodeId,
                    row.SystemId, row.ParentNodeId, (SystemNodeType)row.NodeType, row.EquipmentId, row.SortOrder))
                    .ToArray();
                var availabilityByEquipment = nodes
                    .Where(node => node.SystemId == id && node.NodeType == SystemNodeType.Equipment &&
                        node.EquipmentId.HasValue)
                    .Select(node => node.EquipmentId.Value).Distinct().OrderBy(equipmentId => equipmentId)
                    .ToDictionary(equipmentId => equipmentId, equipmentId =>
                        CalculateEquipment(repository, samples, equipmentId, fromUtc, toUtc).Availability);
                var result = SystemAvailabilityCalculator.Calculate(id, nodes, availabilityByEquipment);

                return Computed(AvailabilityEndpoint, toUtc - fromUtc, availabilityByEquipment.Count, timer, new
                {
                    serverTimeUtc = MetricsPeriod.TruncateMilliseconds(runtime.UtcNow()),
                    system.SystemId,
                    system.Name,
                    availability = AvailabilityValue(result),
                    unavailable = Unavailable(result)
                });
            });
        }

        private bool TryGetPeriod(out DateTime fromUtc, out DateTime toUtc)
        {
            var query = Request.GetQueryNameValuePairs().ToLookup(pair => pair.Key, pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            var from = query["fromUtc"];
            var to = query["toUtc"];
            return MetricsPeriod.TryParse(from.Count() == 1 ? from.Single() : null,
                to.Count() == 1 ? to.Single() : null, runtime.UtcNow(), out fromUtc, out toUtc);
        }

        private EquipmentReliability CalculateEquipment(DashboardReadRepository repository, SampleRepository samples,
            int equipmentId, DateTime fromUtc, DateTime toUtc)
        {
            // Materialize the faults before opening the sample reader on this connection.
            var faults = repository.GetFaultsForMetrics(equipmentId, fromUtc, toUtc)
                .Select(row => new ReliabilityFault(row.AlarmRaisedAtUtc, row.Status, row.RecoveredAtUtc))
                .ToArray();
            var stream = samples.StreamSamples(equipmentId, fromUtc, toUtc)
                .Select(row => new ReliabilitySample(DateTime.SpecifyKind(row.CollectedAtUtc, DateTimeKind.Utc),
                    row.StatusCode));

            return ReliabilityCalculator.Calculate(fromUtc, toUtc,
                TimeSpan.FromSeconds(runtime.Settings.GapThresholdSeconds), stream, faults);
        }

        private static double? AvailabilityValue(MetricResult<double> result)
        {
            return result.HasValue ? result.Value : (double?)null;
        }

        private static object Unavailable<T>(MetricResult<T> result)
        {
            return result.HasValue ? null : new { reason = ReasonName(result.Reason.Value), detail = result.Detail };
        }

        public static string ReasonName(MetricUnavailableReason reason)
        {
            switch (reason)
            {
                case MetricUnavailableReason.NoFaults: return "no_faults";
                case MetricUnavailableReason.NoClosedFaults: return "no_closed_faults";
                case MetricUnavailableReason.ZeroObservedTime: return "zero_observed_time";
                case MetricUnavailableReason.StructureError: return "structure_error";
                case MetricUnavailableReason.ChildUnavailable: return "child_unavailable";
                default: throw new ArgumentOutOfRangeException(nameof(reason));
            }
        }

        private HttpResponseMessage Computed<T>(string endpoint, TimeSpan period, int equipmentCount,
            Stopwatch timer, T payload)
        {
            var response = Request.CreateResponse(HttpStatusCode.OK, payload);
            runtime.Logger.Information("Metrics computed {Endpoint} {PeriodSeconds} {EquipmentCount} {ElapsedMs}",
                endpoint, period.TotalSeconds, equipmentCount, timer.ElapsedMilliseconds);
            return response;
        }

        private HttpResponseMessage Read(string endpoint,
            Func<DashboardReadRepository, SampleRepository, HttpResponseMessage> read)
        {
            try
            {
                using (var connection = new SqlConnection(runtime.Settings.ConnectionString))
                {
                    connection.Open();
                    return read(new DashboardReadRepository(connection), new SampleRepository(connection));
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
