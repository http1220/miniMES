using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Collector
{
    public sealed class CollectorTime
    {
        public Func<TimeSpan> Now { get; }
        public Func<TimeSpan, CancellationToken, Task> Delay { get; }

        private CollectorTime(Stopwatch stopwatch)
        {
            Now = () => stopwatch.Elapsed;
            Delay = Task.Delay;
        }

        public static CollectorTime StartReal()
        {
            return new CollectorTime(Stopwatch.StartNew());
        }
    }
}
