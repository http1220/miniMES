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

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            long consecutiveFailures = 0;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    timeline.ConfirmAll(state);
                    if (!timeline.HasReference)
                    {
                        await delay(IdleInterval, cancellationToken);
                        continue;
                    }

                    var alarms = timeline.NextAlarmBatch(state, MaxBatch);
                    var samples = timeline.NextSampleBatch(state, MaxBatch);
                    if (alarms.Count == 0 && samples.Count == 0)
                    {
                        await delay(IdleInterval, cancellationToken);
                        continue;
                    }

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
                        consecutiveFailures++;
                        await delay(DelayJitter.ApplyJitter(backoff.OnConnectionFailed(), random(),
                            settings.BackoffMaximumJitterFraction), cancellationToken);
                        continue;
                    }

                    backoff.OnConnectionSucceeded();
                    if (consecutiveFailures > 0)
                    {
                        log.Write(LogEventLevel.Information, CollectorEventKind.TransmitRecovered, count: consecutiveFailures);
                        consecutiveFailures = 0;
                    }

                    var interval = alarms.Count == MaxBatch || samples.Count == MaxBatch ? TimeSpan.Zero : IdleInterval;
                    await delay(interval, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
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
