using System;
using System.Collections.Generic;
using System.Linq;

namespace Core
{
    public static class ReliabilityCalculator
    {
        public static EquipmentReliability Calculate(DateTime fromUtc, DateTime toUtc, TimeSpan gapThreshold,
            IEnumerable<ReliabilitySample> samples, IEnumerable<ReliabilityFault> faults)
        {
            ReliabilityTime.RequireUtc(fromUtc, nameof(fromUtc));
            ReliabilityTime.RequireUtc(toUtc, nameof(toUtc));
            if (fromUtc >= toUtc)
                throw new ArgumentOutOfRangeException(nameof(toUtc));
            if (gapThreshold <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(gapThreshold));
            if (samples == null)
                throw new ArgumentNullException(nameof(samples));
            if (faults == null)
                throw new ArgumentNullException(nameof(faults));

            var gaps = new List<ReliabilityInterval>();
            long uptime = CollectGaps(fromUtc.Ticks, toUtc.Ticks, gapThreshold.Ticks, samples, gaps);
            long gapTime = gaps.Sum(gap => gap.EndTicks - gap.StartTicks);
            long observed = (toUtc - fromUtc).Ticks - gapTime;
            return CalculateFaultMetrics(fromUtc, toUtc, uptime, observed, gapTime, gaps, faults);
        }

        private static long CollectGaps(long from, long to, long threshold,
            IEnumerable<ReliabilitySample> samples, List<ReliabilityInterval> gaps)
        {
            long previous = from;
            byte? previousStatus = null;
            long uptime = 0;
            foreach (var sample in samples)
            {
                long current = sample.CollectedAtUtc.Ticks;
                if (current < from || current >= to)
                    throw new ArgumentException("Sample is outside the query period.", nameof(samples));
                if (current < previous)
                    throw new ArgumentException("Samples are out of order.", nameof(samples));

                uptime += ClassifyInterval(previous, current, threshold, previousStatus, gaps);
                previous = current;
                previousStatus = sample.StatusCode;
            }

            return uptime + ClassifyInterval(previous, to, threshold, previousStatus, gaps);
        }

        private static long ClassifyInterval(long start, long end, long threshold, byte? status,
            List<ReliabilityInterval> gaps)
        {
            long length = end - start;
            if (length == 0)
                return 0;

            if (length > threshold || !status.HasValue || status.Value > 2)
            {
                gaps.Add(new ReliabilityInterval(start, end));
                return 0;
            }

            return status == 1 ? length : 0;
        }

        private static EquipmentReliability CalculateFaultMetrics(DateTime from, DateTime to,
            long uptime, long observed, long gapTime, List<ReliabilityInterval> gaps,
            IEnumerable<ReliabilityFault> faults)
        {
            var faultIntervals = new List<ReliabilityInterval>();
            int faultCount = 0;
            int closedCount = 0;
            int unclosedCount = 0;
            // The sum can exceed Int64 even though the average fits in a TimeSpan.
            decimal repairTicks = 0;
            foreach (var fault in faults)
            {
                if (fault.Status == FaultStatus.Cancelled)
                    continue;

                var alarm = fault.AlarmStartedAtUtc;
                bool closed = fault.Status == FaultStatus.Closed;
                if (closed && (!fault.CompletedAtUtc.HasValue || fault.CompletedAtUtc.Value < alarm))
                    throw new ArgumentException("Closed fault requires a valid completion time.", nameof(faults));

                var completed = closed ? fault.CompletedAtUtc.Value : to;
                if (alarm >= from && alarm < to)
                {
                    faultCount++;
                    if (closed)
                    {
                        closedCount++;
                        repairTicks += (completed - alarm).Ticks;
                    }
                    else if (fault.Status == FaultStatus.Registered || fault.Status == FaultStatus.InProgress)
                    {
                        unclosedCount++;
                    }
                }

                long start = Math.Max(from.Ticks, alarm.Ticks);
                long end = Math.Min(to.Ticks, completed.Ticks);
                if (start < end)
                    faultIntervals.Add(new ReliabilityInterval(start, end));
            }

            long down = ReliabilityIntervals.IntersectObserved(ReliabilityIntervals.Merge(faultIntervals), gaps)
                .Sum(interval => interval.EndTicks - interval.StartTicks);
            var mtbf = faultCount == 0
                ? MetricResult<TimeSpan>.Unavailable(MetricUnavailableReason.NoFaults, "no_faults")
                : MetricResult<TimeSpan>.Of(TimeSpan.FromTicks(uptime / faultCount));
            // Remove the remainder before dividing to truncate to whole ticks exactly.
            var mttr = closedCount == 0
                ? MetricResult<TimeSpan>.Unavailable(MetricUnavailableReason.NoClosedFaults, "no_closed_faults")
                : MetricResult<TimeSpan>.Of(TimeSpan.FromTicks(
                    (long)((repairTicks - repairTicks % closedCount) / closedCount)));
            var availability = observed == 0
                ? MetricResult<double>.Unavailable(MetricUnavailableReason.ZeroObservedTime, "zero_observed_time")
                : MetricResult<double>.Of((double)(observed - down) / observed);

            return new EquipmentReliability(mtbf, mttr, availability, TimeSpan.FromTicks(uptime),
                TimeSpan.FromTicks(gapTime), TimeSpan.FromTicks(observed), TimeSpan.FromTicks(down),
                faultCount, closedCount, unclosedCount);
        }
    }
}
