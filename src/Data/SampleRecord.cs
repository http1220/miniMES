using System;

namespace Data
{
    public sealed class SampleRecord
    {
        public Guid RunId { get; set; }
        public long SequenceNumber { get; set; }
        public int EquipmentId { get; set; }
        public DateTime CollectedAtUtc { get; set; }
        public float? Temperature { get; set; }
        public float? Vibration { get; set; }
        public byte? StatusCode { get; set; }
    }
}
