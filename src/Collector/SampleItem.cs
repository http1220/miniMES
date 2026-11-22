using System;

namespace Collector
{
    public sealed class SampleItem
    {
        public int EquipmentId { get; }
        public long SequenceNumber { get; }
        public TimeSpan ReceivedAt { get; }
        public float? Temperature { get; }
        public float? Vibration { get; }
        public byte? StatusCode { get; }

        public SampleItem(int equipmentId, long sequenceNumber, TimeSpan receivedAt,
            float? temperature, float? vibration, byte? statusCode)
        {
            EquipmentId = equipmentId;
            SequenceNumber = sequenceNumber;
            ReceivedAt = receivedAt;
            Temperature = temperature;
            Vibration = vibration;
            StatusCode = statusCode;
        }
    }
}
