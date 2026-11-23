using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog.Events;

namespace Collector
{
    public sealed class CollectorRuntime
    {
        private static readonly TimeSpan FatalCleanupTimeout = TimeSpan.FromSeconds(2);

        private readonly CollectorSettings settings;
        private readonly CollectorState state;
        private readonly CollectorLog log;
        private readonly Func<EquipmentSettings, IModbusReader> readerFactory;
        private readonly Func<TimeSpan> now;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private readonly Func<int, Func<double>> randomFactory;
        private readonly IServerClient serverClient;
        private readonly TransmissionTimeline timeline;

        public CollectorRuntime(CollectorSettings settings, CollectorState state, CollectorLog log,
            Func<EquipmentSettings, IModbusReader> readerFactory, Func<TimeSpan> now,
            Func<TimeSpan, CancellationToken, Task> delay, Func<int, Func<double>> randomFactory)
        {
            this.settings = settings;
            this.state = state;
            this.log = log;
            this.readerFactory = readerFactory;
            this.now = now;
            this.delay = delay;
            this.randomFactory = randomFactory;
        }

        public CollectorRuntime(CollectorSettings settings, CollectorState state, CollectorLog log,
            Func<EquipmentSettings, IModbusReader> readerFactory, Func<TimeSpan> now,
            Func<TimeSpan, CancellationToken, Task> delay, Func<int, Func<double>> randomFactory,
            IServerClient serverClient, TransmissionTimeline timeline)
            : this(settings, state, log, readerFactory, now, delay, randomFactory)
        {
            this.serverClient = serverClient;
            this.timeline = timeline;
        }

        public Task RunAsync(CancellationToken cancellationToken)
        {
            var tracker = new ReceiveDelayTracker();
            var equipment = settings.Equipment;
            var loops = new EquipmentLoop[equipment.Length];
            for (int i = 0; i < equipment.Length; i++)
            {
                var device = equipment[i];
                loops[i] = new EquipmentLoop(device, settings, state, readerFactory(device), now,
                    delay, log, randomFactory(device.Id), tracker);
            }

            bool runServerLoops = serverClient != null && timeline != null;
            var tasks = new Task[loops.Length + (runServerLoops ? 3 : 1)];
            for (int i = 0; i < loops.Length; i++)
                tasks[i] = loops[i].RunAsync(cancellationToken);
            tasks[loops.Length] = new JudgeLoop(settings, state, now, delay, log, tracker)
                .RunAsync(cancellationToken);

            if (runServerLoops)
            {
                tasks[loops.Length + 1] = new TransmitLoop(settings, state, timeline, serverClient, now,
                    delay, log, randomFactory(0)).RunAsync(cancellationToken);
                tasks[loops.Length + 2] = new HeartbeatLoop(settings, state, timeline, serverClient, now,
                    delay, log).RunAsync(cancellationToken);
            }

            // A future supervisor will cancel sibling loops when one faults.
            return Task.WhenAll(tasks);
        }

        public async Task<int> RunSupervisedAsync(CancellationToken shutdownToken)
        {
            var shutdown = new TaskCompletionSource<TimeSpan>();
            using (shutdownToken.Register(() => shutdown.TrySetResult(now() + settings.ShutdownTimeout)))
            using (var loopCts = new CancellationTokenSource())
            using (var transmitCts = new CancellationTokenSource())
            {
                var transmit = serverClient != null && timeline != null
                    ? new TransmitLoop(settings, state, timeline, serverClient, now, delay, log, randomFactory(0))
                    : null;
                var pending = new List<(string Name, Task Task)>();
                if (!shutdown.Task.IsCompleted)
                {
                    var tracker = new ReceiveDelayTracker();
                    var equipment = settings.Equipment;
                    var equipmentLoops = equipment.Select(device => new EquipmentLoop(device, settings, state,
                        readerFactory(device), now, delay, log, randomFactory(device.Id), tracker)).ToArray();
                    var judge = new JudgeLoop(settings, state, now, delay, log, tracker);
                    var heartbeat = transmit != null
                        ? new HeartbeatLoop(settings, state, timeline, serverClient, now, delay, log)
                        : null;

                    for (int i = 0; i < equipmentLoops.Length; i++)
                        pending.Add(("Equipment." + equipment[i].Id, equipmentLoops[i].RunAsync(loopCts.Token)));
                    pending.Add(("Judge", judge.RunAsync(loopCts.Token)));
                    if (transmit != null)
                    {
                        pending.Add(("Transmit", transmit.RunAsync(transmitCts.Token)));
                        pending.Add(("Heartbeat", heartbeat.RunAsync(loopCts.Token)));
                    }
                }

                var first = await Task.WhenAny(new Task[] { shutdown.Task }.Concat(pending.Select(loop => loop.Task)));
                if (first != shutdown.Task)
                {
                    var failed = pending.First(loop => loop.Task == first);
                    LogFatal(failed.Name, first);
                    pending.Remove(failed);
                    loopCts.Cancel();
                    transmitCts.Cancel();
                    await ObserveWithinCleanupAsync(pending);
                    return ExitCodes.FatalLoop;
                }

                var deadline = await shutdown.Task;
                log.Write(LogEventLevel.Information, CollectorEventKind.ShutdownRequested);
                loopCts.Cancel();
                transmitCts.Cancel();
                using (var deadlineCts = new CancellationTokenSource())
                {
                    var remaining = deadline - now();
                    var timeout = remaining > TimeSpan.Zero ? delay(remaining, deadlineCts.Token) : Task.CompletedTask;
                    try
                    {
                        return await FinishShutdownAsync(pending, transmit, timeout);
                    }
                    finally
                    {
                        deadlineCts.Cancel();
                    }
                }
            }
        }

        private async Task<int> FinishShutdownAsync(List<(string Name, Task Task)> pending,
            TransmitLoop transmit, Task timeout)
        {
            while (pending.Count != 0)
            {
                await Task.WhenAny(pending.Select(loop => loop.Task).Concat(new[] { timeout }));
                var completed = pending.FindIndex(loop => loop.Task.IsCompleted);
                if (completed < 0)
                {
                    // Keep observing outstanding work after the shutdown deadline.
                    foreach (var loop in pending)
                        _ = ObserveFailureAsync(loop.Name, loop.Task);
                    return LogIncomplete();
                }

                var loopResult = pending[completed];
                pending.RemoveAt(completed);
                if (loopResult.Task.Status != TaskStatus.RanToCompletion)
                {
                    LogFatal(loopResult.Name, loopResult.Task);
                    await ObserveWithinCleanupAsync(pending);
                    return ExitCodes.FatalLoop;
                }
            }

            if (transmit == null)
                return RemainingCount == 0 ? LogCompleted() : LogIncomplete();
            if (timeout.IsCompleted)
                return LogIncomplete();

            using (var drainCts = new CancellationTokenSource())
            {
                var drain = transmit.DrainAsync(drainCts.Token);
                var first = await Task.WhenAny(drain, timeout);
                if (first == timeout)
                {
                    drainCts.Cancel();
                    await WaitForCleanupAsync(drain);
                    if (!drain.IsCompleted)
                    {
                        _ = ObserveFailureAsync("Transmit", drain);
                        return LogIncomplete();
                    }
                }

                if (drain.IsCompleted && drain.Status != TaskStatus.RanToCompletion)
                {
                    LogFatal("Transmit", drain);
                    return ExitCodes.FatalLoop;
                }

                return first == drain ? LogCompleted() : LogIncomplete();
            }
        }

        private Task ObserveWithinCleanupAsync(IEnumerable<(string Name, Task Task)> pending) =>
            WaitForCleanupAsync(Task.WhenAll(pending.Select(loop => ObserveFailureAsync(loop.Name, loop.Task))));

        private async Task WaitForCleanupAsync(Task task)
        {
            if (task.IsCompleted)
                return;

            using (var cleanupCts = new CancellationTokenSource())
            {
                try
                {
                    await Task.WhenAny(task, delay(FatalCleanupTimeout, cleanupCts.Token));
                }
                finally
                {
                    cleanupCts.Cancel();
                }
            }
        }

        private async Task ObserveFailureAsync(string name, Task task)
        {
            try
            {
                await task;
            }
            catch (Exception error)
            {
                log.Write(LogEventLevel.Warning, CollectorEventKind.ExceptionObserved, exception: error, detail: name);
            }
        }

        private void LogFatal(string name, Task task)
        {
            string failureType = task.IsCanceled ? "UnexpectedCancel"
                : task.IsFaulted ? null : "UnexpectedExit";
            log.Write(LogEventLevel.Fatal, CollectorEventKind.LoopFatal,
                failureType: failureType, exception: task.Exception?.InnerException, detail: name);
        }

        private long RemainingCount => (long)state.SampleCount + state.AlarmCount + state.UnsentAlarmCount;

        private int LogIncomplete()
        {
            log.Write(LogEventLevel.Warning, CollectorEventKind.ShutdownIncomplete, count: RemainingCount);
            return ExitCodes.ShutdownIncomplete;
        }

        private int LogCompleted()
        {
            log.Write(LogEventLevel.Information, CollectorEventKind.ShutdownCompleted);
            return ExitCodes.Success;
        }
    }
}
