using System;
using System.Threading;
using System.Threading.Tasks;
using Core;
using Serilog.Events;

namespace Collector
{
    public sealed class JudgeLoop
    {
        private const int SnapshotEveryTicks = 10;
        private readonly CollectorSettings settings;
        private readonly EquipmentSettings[] equipment;
        private readonly CollectorState state;
        private readonly Func<TimeSpan> now;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private readonly CollectorLog log;
        private readonly ReceiveDelayTracker receiveDelay;

        public JudgeLoop(CollectorSettings settings, CollectorState state, Func<TimeSpan> now,
            Func<TimeSpan, CancellationToken, Task> delay, CollectorLog log)
            : this(settings, state, now, delay, log, null)
        {
        }

        internal JudgeLoop(CollectorSettings settings, CollectorState state, Func<TimeSpan> now,
            Func<TimeSpan, CancellationToken, Task> delay, CollectorLog log, ReceiveDelayTracker receiveDelay)
        {
            this.settings = settings;
            equipment = settings.Equipment;
            this.state = state;
            this.now = now;
            this.delay = delay;
            this.log = log;
            this.receiveDelay = receiveDelay;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            int ticks = 0;
            long samplesDropped = 0;
            long alarmsRejected = 0;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await delay(settings.JudgeInterval, cancellationToken);
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    foreach (var device in equipment)
                    {
                        var observedAt = now();
                        AlarmSubmissionLog.Write(log, device.Id, AlarmCause.Communication,
                            state.CheckCommunicationTimeoutAndSubmit(device.Id, observedAt, settings.CommunicationFault));
                    }

                    int moved = state.RequeueUnsent();
                    if (moved > 0)
                        log.Write(LogEventLevel.Information, CollectorEventKind.AlarmUnsentRequeued, count: moved);

                    long dropped = state.SamplesDropped;
                    if (dropped > samplesDropped)
                        log.Write(LogEventLevel.Warning, CollectorEventKind.SampleBufferDropped, count: dropped);
                    samplesDropped = dropped;

                    long rejected = state.AlarmsRejected;
                    if (rejected > alarmsRejected)
                        log.Write(LogEventLevel.Warning, CollectorEventKind.AlarmQueueRejected, count: rejected);
                    alarmsRejected = rejected;

                    if (++ticks == SnapshotEveryTicks)
                    {
                        foreach (var device in equipment)
                            log.Write(LogEventLevel.Information, CollectorEventKind.StatusSnapshot, device.Id,
                                count: state.LastSampleSequence(device.Id));
                        ticks = 0;
                    }

                    long milliseconds = receiveDelay?.TakeMaximumMilliseconds() ?? 0;
                    if (milliseconds != 0)
                        log.Write(LogEventLevel.Information, CollectorEventKind.ReceiveDelay, count: milliseconds);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Other exceptions propagate to the runtime for supervision.
            }
        }
    }
}
