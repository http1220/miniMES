using System;

namespace Data
{
    public sealed class FaultTransitionContext
    {
        internal FaultTransitionContext(byte status, DateTime alarmRaisedAtUtc)
        {
            Status = status;
            AlarmRaisedAtUtc = alarmRaisedAtUtc;
        }

        public byte Status { get; }
        public DateTime AlarmRaisedAtUtc { get; }
    }
}
