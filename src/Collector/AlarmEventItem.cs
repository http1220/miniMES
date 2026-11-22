using System;

namespace Collector
{
    public enum AlarmCause : byte { Communication = 1, Temperature = 2, Vibration = 3 }
    public enum AlarmEventType : byte { Raised = 1, Cleared = 2, ReportAbnormal = 3, ReportNormal = 4 }

    public sealed class AlarmEventItem
    {
        public long EventSequence { get; }
        public int EquipmentId { get; }
        public AlarmCause Cause { get; }
        public AlarmEventType EventType { get; }
        public TimeSpan OccurredAt { get; }

        public AlarmEventItem(long eventSequence, int equipmentId, AlarmCause cause,
            AlarmEventType eventType, TimeSpan occurredAt)
        {
            EventSequence = eventSequence;
            EquipmentId = equipmentId;
            Cause = cause;
            EventType = eventType;
            OccurredAt = occurredAt;
        }
    }
}
