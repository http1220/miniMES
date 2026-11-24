using System;
using System.Collections.Generic;
using System.Linq;

namespace Core
{
    public readonly struct ReliabilityInterval
    {
        public long StartTicks { get; }
        public long EndTicks { get; }

        public ReliabilityInterval(long startTicks, long endTicks)
        {
            if (endTicks < startTicks)
                throw new ArgumentOutOfRangeException(nameof(endTicks));

            StartTicks = startTicks;
            EndTicks = endTicks;
        }
    }

    public static class ReliabilityIntervals
    {
        public static IReadOnlyList<ReliabilityInterval> Merge(IEnumerable<ReliabilityInterval> intervals)
        {
            var merged = new List<ReliabilityInterval>();
            foreach (var interval in intervals.OrderBy(interval => interval.StartTicks))
            {
                if (interval.StartTicks == interval.EndTicks)
                    continue;

                int last = merged.Count - 1;
                if (last < 0 || merged[last].EndTicks < interval.StartTicks)
                    merged.Add(interval);
                else if (merged[last].EndTicks < interval.EndTicks)
                    merged[last] = new ReliabilityInterval(merged[last].StartTicks, interval.EndTicks);
            }

            return merged;
        }

        // Both inputs are sorted and internally disjoint. Removing gaps intersects
        // the merged fault intervals with the complement of the gaps.
        public static IReadOnlyList<ReliabilityInterval> IntersectObserved(
            IReadOnlyList<ReliabilityInterval> mergedFaults, IReadOnlyList<ReliabilityInterval> gaps)
        {
            var observedFaults = new List<ReliabilityInterval>();
            int gapIndex = 0;
            foreach (var fault in mergedFaults)
            {
                long start = fault.StartTicks;
                while (gapIndex < gaps.Count && gaps[gapIndex].StartTicks < fault.EndTicks)
                {
                    var gap = gaps[gapIndex];
                    if (gap.StartTicks == gap.EndTicks)
                    {
                        gapIndex++;
                        continue;
                    }

                    if (start < gap.StartTicks)
                        observedFaults.Add(new ReliabilityInterval(start, gap.StartTicks));

                    start = Math.Max(start, gap.EndTicks);
                    if (gap.EndTicks > fault.EndTicks)
                        break;

                    gapIndex++;
                }

                if (start < fault.EndTicks)
                    observedFaults.Add(new ReliabilityInterval(start, fault.EndTicks));
            }

            return observedFaults;
        }
    }
}
