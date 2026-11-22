using System;
using System.Threading;

namespace Collector
{
    internal sealed class ReceiveDelayTracker
    {
        private long maximumTicks;

        internal void Record(TimeSpan elapsed)
        {
            long ticks = elapsed.Ticks;
            long current = Interlocked.Read(ref maximumTicks);
            while (ticks > current)
            {
                long previous = Interlocked.CompareExchange(ref maximumTicks, ticks, current);
                if (previous == current)
                    return;
                current = previous;
            }
        }

        internal long TakeMaximumMilliseconds()
        {
            long ticks = Interlocked.Exchange(ref maximumTicks, 0);
            return ticks / TimeSpan.TicksPerMillisecond;
        }
    }
}
