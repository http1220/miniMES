using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnitTests
{
    public sealed class VirtualScheduler
    {
        private readonly object gate = new object();
        private readonly List<Waiter> pending = new List<Waiter>();
        private readonly ContinuationContext context = new ContinuationContext();
        private TimeSpan now;
        private long advanceNumber;
        private bool advancing;

        public TimeSpan Now { get { lock (gate) return now; } }
        public Func<TimeSpan> NowProvider => () => Now;
        public Func<TimeSpan, CancellationToken, Task> DelayProvider => Delay;

        public Task Delay(TimeSpan d, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return Task.FromCanceled(ct);

            Waiter waiter;
            lock (gate)
            {
                waiter = new Waiter(d > TimeSpan.Zero ? now + d : now,
                    d > TimeSpan.Zero ? advanceNumber : advanceNumber + 1);
                pending.Add(waiter);
            }

            var registration = ct.Register(() =>
            {
                lock (gate)
                {
                    if (!pending.Remove(waiter))
                        return;
                }
                waiter.Completion.TrySetCanceled();
            });

            lock (gate)
            {
                if (pending.Contains(waiter))
                {
                    waiter.Registration = registration;
                    return waiter.Completion.Task;
                }
            }

            registration.Dispose();
            return waiter.Completion.Task;
        }

        // Start loops here to dispatch their captured continuations in registration order.
        public Task RunAsync(Func<Task> loop)
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try { return loop(); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        public async Task AdvanceAsync(TimeSpan delta)
        {
            if (delta < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(delta));

            TimeSpan target;
            lock (gate)
            {
                if (advancing)
                    throw new InvalidOperationException("A virtual advance is already in progress.");
                target = now + delta;
                advancing = true;
                advanceNumber++;
            }

            try
            {
                while (true)
                {
                    context.Drain();
                    var due = new List<Waiter>();
                    lock (gate)
                    {
                        TimeSpan? next = null;
                        foreach (var waiter in pending)
                        {
                            if (waiter.AdvanceNumber > advanceNumber || waiter.Due > target)
                                continue;
                            if (!next.HasValue || waiter.Due < next.Value)
                            {
                                next = waiter.Due;
                                due.Clear();
                            }
                            if (waiter.Due == next.Value)
                                due.Add(waiter);
                        }

                        if (!next.HasValue)
                        {
                            now = target;
                            return;
                        }
                        if (next.Value > now)
                            now = next.Value;
                        foreach (var waiter in due)
                            pending.Remove(waiter);
                    }

                    foreach (var waiter in due)
                    {
                        waiter.Registration.Dispose();
                        waiter.Completion.TrySetResult(true);
                    }

                    if (!context.Drain())
                    {
                        // Give continuations that bypass the context a chance to register another delay.
                        await Task.Yield();
                        await Task.Delay(1).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                lock (gate)
                    advancing = false;
            }
        }

        private sealed class Waiter
        {
            internal readonly TimeSpan Due;
            internal readonly long AdvanceNumber;
            internal readonly TaskCompletionSource<bool> Completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal CancellationTokenRegistration Registration;

            internal Waiter(TimeSpan due, long advanceNumber)
            {
                Due = due;
                AdvanceNumber = advanceNumber;
            }
        }

        private sealed class ContinuationContext : SynchronizationContext
        {
            private readonly Queue<(SendOrPostCallback Callback, object State)> callbacks =
                new Queue<(SendOrPostCallback Callback, object State)>();

            public override void Post(SendOrPostCallback callback, object state)
            {
                lock (callbacks)
                    callbacks.Enqueue((callback, state));
            }

            internal bool Drain()
            {
                var previous = Current;
                SetSynchronizationContext(this);
                var ran = false;
                try
                {
                    while (true)
                    {
                        (SendOrPostCallback Callback, object State) next;
                        lock (callbacks)
                        {
                            if (callbacks.Count == 0)
                                return ran;
                            next = callbacks.Dequeue();
                        }
                        ran = true;
                        next.Callback(next.State);
                    }
                }
                finally
                {
                    SetSynchronizationContext(previous);
                }
            }
        }
    }
}
