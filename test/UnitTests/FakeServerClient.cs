using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Collector;

namespace UnitTests
{
    internal sealed class FakeServerClient : IServerClient
    {
        internal static readonly DateTime Epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly VirtualScheduler scheduler;
        internal List<Call> Calls { get; } = new List<Call>();
        internal Func<Call, CancellationToken, Task<ServerResult>> Respond { get; set; }

        internal FakeServerClient(VirtualScheduler scheduler)
        {
            this.scheduler = scheduler;
            Respond = (call, ct) => Task.FromResult(Success());
        }

        internal ServerResult Success() => new ServerResult(ServerOutcome.Success, scheduler.Now, Epoch + scheduler.Now);

        public Task<ServerResult> SendAlarmEventsAsync(Guid runId, DateTime startedAtUtc,
            IReadOnlyList<ConfirmedAlarmEvent> events, CancellationToken cancellationToken) =>
            Record(new Call("Alarm", scheduler.Now, runId, startedAtUtc, events, null), cancellationToken);

        public Task<ServerResult> SendSamplesAsync(Guid runId, DateTime startedAtUtc,
            IReadOnlyList<ConfirmedSample> samples, CancellationToken cancellationToken) =>
            Record(new Call("Sample", scheduler.Now, runId, startedAtUtc, null, samples), cancellationToken);

        public Task<ServerResult> SendHeartbeatAsync(Guid runId, CancellationToken cancellationToken) =>
            Record(new Call("Heartbeat", scheduler.Now, runId, null, null, null), cancellationToken);

        private Task<ServerResult> Record(Call call, CancellationToken cancellationToken)
        {
            Calls.Add(call);
            return Respond(call, cancellationToken);
        }

        internal sealed class Call
        {
            internal string Kind { get; }
            internal TimeSpan At { get; }
            internal Guid RunId { get; }
            internal DateTime? StartedAtUtc { get; }
            internal IReadOnlyList<ConfirmedAlarmEvent> Alarms { get; }
            internal IReadOnlyList<ConfirmedSample> Samples { get; }

            internal Call(string kind, TimeSpan at, Guid runId, DateTime? startedAtUtc,
                IReadOnlyList<ConfirmedAlarmEvent> alarms, IReadOnlyList<ConfirmedSample> samples)
            {
                Kind = kind;
                At = at;
                RunId = runId;
                StartedAtUtc = startedAtUtc;
                Alarms = alarms;
                Samples = samples;
            }
        }
    }
}
