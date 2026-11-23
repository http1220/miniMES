using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core;
using Serilog.Events;

namespace Collector
{
    public sealed class TransmitLoop
    {
        private const int MaxBatch = 25;
        private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(1);
        private readonly CollectorSettings settings;
        private readonly CollectorState state;
        private readonly TransmissionTimeline timeline;
        private readonly IServerClient client;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private readonly CollectorLog log;
        private readonly Func<double> random;
        private readonly ConnectionBackoff backoff;

        public TransmitLoop(CollectorSettings settings, CollectorState state, TransmissionTimeline timeline,
            IServerClient client, Func<TimeSpan> now, Func<TimeSpan, CancellationToken, Task> delay,
            CollectorLog log, Func<double> random)
        {
            this.settings = settings;
            this.state = state;
            this.timeline = timeline;
            this.client = client;
            this.delay = delay;
            this.log = log;
            this.random = random;
            backoff = new ConnectionBackoff(settings.BackoffMaximum);
        }

        public Task RunAsync(CancellationToken cancellationToken) =>
            RunCyclesAsync(false, cancellationToken);

        public Task DrainAsync(CancellationToken cancellationToken) =>
            RunCyclesAsync(true, cancellationToken);

        private bool IsEmpty => state.SampleCount == 0 && state.AlarmCount == 0 && state.UnsentAlarmCount == 0;

        private async Task RunCyclesAsync(bool draining, CancellationToken cancellationToken)
        {
            long consecutiveFailures = 0;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (draining)
                    {
                        if (IsEmpty)
                            return;
                        state.RequeueUnsent();
                    }

                    var cycle = await RunCycleAsync(consecutiveFailures, cancellationToken);
                    if (draining && IsEmpty)
                        return;

                    consecutiveFailures = cycle.ConsecutiveFailures;
                    await delay(cycle.Interval, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async Task<(TimeSpan Interval, long ConsecutiveFailures)> RunCycleAsync(
            long consecutiveFailures, CancellationToken cancellationToken)
        {
            timeline.ConfirmAll(state);
            if (!timeline.HasReference)
                return (IdleInterval, consecutiveFailures);

            var alarms = timeline.NextAlarmBatch(state, MaxBatch);
            var samples = timeline.NextSampleBatch(state, MaxBatch);
            if (alarms.Count == 0 && samples.Count == 0)
                return (IdleInterval, consecutiveFailures);

            bool alarmHandled = false;
            bool sampleHandled = false;
            var startedAtUtc = timeline.StartedAtUtc.Value;
            if (alarms.Count != 0)
            {
                var result = await client.SendAlarmEventsAsync(state.RunId, startedAtUtc, alarms, cancellationToken);
                alarmHandled = ProcessResult(result, alarms.Count, "Alarm", () =>
                    state.RemoveAlarmEvents(new HashSet<long>(alarms.Select(alarm => alarm.Item.EventSequence))));
            }
            if (samples.Count != 0)
            {
                var result = await client.SendSamplesAsync(state.RunId, startedAtUtc, samples, cancellationToken);
                sampleHandled = ProcessResult(result, samples.Count, "Sample", () =>
                    state.RemoveSamples(new HashSet<(int, long)>(samples.Select(sample =>
                        (sample.Item.EquipmentId, sample.Item.SequenceNumber)))));
                if (result.Outcome == ServerOutcome.Success)
                    log.Write(LogEventLevel.Information, CollectorEventKind.SampleStored, count: samples.Count);
            }

            if (!alarmHandled && !sampleHandled)
            {
                var retryInterval = DelayJitter.ApplyJitter(backoff.OnConnectionFailed(), random(),
                    settings.BackoffMaximumJitterFraction);
                return (retryInterval, consecutiveFailures + 1);
            }

            backoff.OnConnectionSucceeded();
            if (consecutiveFailures > 0)
                log.Write(LogEventLevel.Information, CollectorEventKind.TransmitRecovered, count: consecutiveFailures);

            var interval = alarms.Count == MaxBatch || samples.Count == MaxBatch ? TimeSpan.Zero : IdleInterval;
            return (interval, 0);
        }

        private bool ProcessResult(ServerResult result, int count, string detail, Action remove)
        {
            switch (result.Outcome)
            {
                case ServerOutcome.Success:
                    CollectorLog.WriteClockJump(log, timeline.UpdateReference(result.ReceivedAt.Value, result.ServerTimeUtc.Value));
                    remove();
                    return true;
                case ServerOutcome.BadRequest:
                    remove();
                    log.Write(LogEventLevel.Error, CollectorEventKind.BatchDiscarded,
                        failureType: result.ErrorKind ?? "BadRequest", count: count, detail: detail);
                    return true;
                default:
                    log.Write(LogEventLevel.Warning, CollectorEventKind.TransmitFailed,
                        failureType: result.Outcome.ToString(), count: result.StatusCode, detail: detail);
                    return false;
            }
        }
    }
}
