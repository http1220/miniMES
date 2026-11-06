using System;

namespace Data
{
    public sealed class AlarmEventRecord
    {
        public Guid RunId { get; set; }
        public long EventSequence { get; set; }
        public int EquipmentId { get; set; }
        public byte Cause { get; set; }
        public byte EventType { get; set; }
        public DateTime OccurredAtUtc { get; set; }
    }
}
