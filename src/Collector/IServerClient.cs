using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Collector
{
    public enum ServerOutcome { Success, BadRequest, AuthFailed, ServerError, Timeout, ConnectionFailed, CertificateFailed }

    public sealed class ServerResult
    {
        public ServerOutcome Outcome { get; }
        public TimeSpan? ReceivedAt { get; }
        public DateTime? ServerTimeUtc { get; }
        public int? StatusCode { get; }
        public string ErrorKind { get; }

        public ServerResult(ServerOutcome outcome, TimeSpan? receivedAt = null, DateTime? serverTimeUtc = null,
            int? statusCode = null, string errorKind = null)
        {
            Outcome = outcome;
            ReceivedAt = receivedAt;
            ServerTimeUtc = serverTimeUtc;
            StatusCode = statusCode;
            ErrorKind = errorKind;
        }
    }

    public interface IServerClient
    {
        Task<ServerResult> SendAlarmEventsAsync(Guid runId, DateTime startedAtUtc,
            IReadOnlyList<ConfirmedAlarmEvent> events, CancellationToken cancellationToken);

        Task<ServerResult> SendSamplesAsync(Guid runId, DateTime startedAtUtc,
            IReadOnlyList<ConfirmedSample> samples, CancellationToken cancellationToken);

        Task<ServerResult> SendHeartbeatAsync(Guid runId, CancellationToken cancellationToken);
    }
}
