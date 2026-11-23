using System;
using System.Threading;
using System.Threading.Tasks;

namespace Collector
{
    public sealed class CollectorRuntime
    {
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
    }
}
