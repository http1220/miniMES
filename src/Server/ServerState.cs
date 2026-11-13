using System;
using System.Threading;
using System.Threading.Tasks;

namespace Server
{
    public sealed class CollectorState
    {
        private readonly object gate = new object();
        private readonly Func<TimeSpan> monotonic;
        private readonly TimeSpan started;
        private readonly TimeSpan stoppedAfter;
        private TimeSpan lastReceived;
        private DateTime? lastUtc;

        public CollectorState(Func<TimeSpan> monotonic, TimeSpan stoppedAfter)
        {
            this.monotonic = monotonic;
            this.stoppedAfter = stoppedAfter;
            started = monotonic();
        }

        public void Receive(DateTime utc)
        {
            lock (gate) { lastReceived = monotonic(); lastUtc = utc; }
        }

        public CollectorSnapshot Read()
        {
            lock (gate)
            {
                var elapsed = monotonic() - (lastUtc.HasValue ? lastReceived : started);
                return new CollectorSnapshot
                {
                    State = elapsed >= stoppedAfter ? "stopped" : lastUtc.HasValue ? "running" : "checking",
                    LastHeartbeatUtc = lastUtc
                };
            }
        }
    }

    public sealed class CollectorSnapshot
    {
        public string State { get; set; }
        public DateTime? LastHeartbeatUtc { get; set; }
    }

    public sealed class DatabaseMonitor : IDisposable
    {
        private readonly object gate = new object();
        private readonly Func<bool> probe;
        private readonly TimeSpan interval;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private Task worker;
        private bool running;
        private bool disposed;
        private long generation;
        private string state = "checking";

        public DatabaseMonitor(Func<bool> probe, TimeSpan interval)
        {
            if (probe == null) throw new ArgumentNullException(nameof(probe));
            if (interval <= TimeSpan.Zero || interval.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(interval));
            this.probe = probe;
            this.interval = interval;
        }

        public string State { get { lock (gate) return state; } }

        public void Start()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(DatabaseMonitor));
                if (!running && state != "connected") Launch();
            }
        }

        public void MarkDisconnected()
        {
            lock (gate)
            {
                if (disposed) return;
                state = "disconnected";
                generation++;
                if (!running) Launch();
            }
        }

        private void Launch()
        {
            running = true;
            worker = Task.Run(async () =>
            {
                try
                {
                    while (!cancellation.IsCancellationRequested)
                    {
                        long observed;
                        lock (gate) observed = generation;
                        bool connected;
                        try { connected = probe(); }
                        catch (Exception) { connected = false; }
                        lock (gate)
                        {
                            if (disposed) return;
                            if (connected && observed == generation)
                            {
                                state = "connected";
                                running = false;
                                return;
                            }
                            state = "disconnected";
                        }
                        await Task.Delay(interval, cancellation.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                finally { lock (gate) { if (disposed) running = false; } }
            });
        }

        public void Dispose()
        {
            Task pending;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                cancellation.Cancel();
                pending = worker;
            }
            if (pending != null) pending.GetAwaiter().GetResult();
            cancellation.Dispose();
        }
    }
}
