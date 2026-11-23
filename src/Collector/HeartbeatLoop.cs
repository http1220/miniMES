using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog.Events;

namespace Collector
{
    public sealed class HeartbeatLoop
    {
        private readonly CollectorSettings settings;
        private readonly CollectorState state;
        private readonly TransmissionTimeline timeline;
        private readonly IServerClient client;
        private readonly Func<TimeSpan> now;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private readonly CollectorLog log;

        public HeartbeatLoop(CollectorSettings settings, CollectorState state, TransmissionTimeline timeline,
            IServerClient client, Func<TimeSpan> now, Func<TimeSpan, CancellationToken, Task> delay, CollectorLog log)
        {
            this.settings = settings;
            this.state = state;
            this.timeline = timeline;
            this.client = client;
            this.now = now;
            this.delay = delay;
            this.log = log;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            bool failed = false;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var cycleStart = now();
                    var result = await client.SendHeartbeatAsync(state.RunId, cancellationToken);
                    if (result.Outcome == ServerOutcome.Success)
                    {
                        CollectorLog.WriteClockJump(log, timeline.UpdateReference(result.ReceivedAt.Value, result.ServerTimeUtc.Value));
                        if (failed)
                        {
                            log.Write(LogEventLevel.Information, CollectorEventKind.HeartbeatRecovered);
                            failed = false;
                        }
                    }
                    else if (!failed)
                    {
                        log.Write(LogEventLevel.Warning, CollectorEventKind.HeartbeatFailed,
                            failureType: result.Outcome.ToString());
                        failed = true;
                    }

                    var remaining = cycleStart + settings.HeartbeatInterval - now();
                    await delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }
}
