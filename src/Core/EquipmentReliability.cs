using System;

namespace Core
{
    public sealed class EquipmentReliability
    {
        public MetricResult<TimeSpan> Mtbf { get; }
        public MetricResult<TimeSpan> Mttr { get; }
        public MetricResult<double> Availability { get; }
        public TimeSpan UptimeDuration { get; }
        public TimeSpan GapDuration { get; }
        public TimeSpan ObservedDuration { get; }
        public TimeSpan FaultDownDuration { get; }
        public int FaultCount { get; }
        public int ClosedFaultCount { get; }
        public int UnclosedFaultCount { get; }

        internal EquipmentReliability(MetricResult<TimeSpan> mtbf, MetricResult<TimeSpan> mttr,
            MetricResult<double> availability, TimeSpan uptimeDuration, TimeSpan gapDuration,
            TimeSpan observedDuration, TimeSpan faultDownDuration, int faultCount,
            int closedFaultCount, int unclosedFaultCount)
        {
            Mtbf = mtbf;
            Mttr = mttr;
            Availability = availability;
            UptimeDuration = uptimeDuration;
            GapDuration = gapDuration;
            ObservedDuration = observedDuration;
            FaultDownDuration = faultDownDuration;
            FaultCount = faultCount;
            ClosedFaultCount = closedFaultCount;
            UnclosedFaultCount = unclosedFaultCount;
        }
    }
}
