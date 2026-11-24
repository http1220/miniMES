using System;

namespace Core
{
    public sealed class ReliabilitySample
    {
        public DateTime CollectedAtUtc { get; }
        public byte? StatusCode { get; }

        public ReliabilitySample(DateTime collectedAtUtc, byte? statusCode)
        {
            ReliabilityTime.RequireUtc(collectedAtUtc, nameof(collectedAtUtc));
            CollectedAtUtc = collectedAtUtc;
            StatusCode = statusCode;
        }
    }

    public sealed class ReliabilityFault
    {
        public DateTime AlarmStartedAtUtc { get; }
        public FaultStatus Status { get; }
        public DateTime? CompletedAtUtc { get; }

        public ReliabilityFault(DateTime alarmStartedAtUtc, FaultStatus status, DateTime? completedAtUtc)
        {
            ReliabilityTime.RequireUtc(alarmStartedAtUtc, nameof(alarmStartedAtUtc));
            if (completedAtUtc.HasValue)
                ReliabilityTime.RequireUtc(completedAtUtc.Value, nameof(completedAtUtc));

            AlarmStartedAtUtc = alarmStartedAtUtc;
            Status = status;
            CompletedAtUtc = completedAtUtc;
        }
    }

    internal static class ReliabilityTime
    {
        internal static void RequireUtc(DateTime value, string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Time must be UTC.", parameterName);
        }
    }
}
