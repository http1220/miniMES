namespace Data
{
    public enum FaultTransitionStatus
    {
        Success,
        StatusMismatch
    }

    public sealed class FaultTransitionResult
    {
        internal FaultTransitionResult(FaultTransitionStatus status, byte currentStatus)
        {
            Status = status;
            CurrentStatus = currentStatus;
        }

        public FaultTransitionStatus Status { get; }

        // 1 = registered, 2 = in progress, 3 = closed, 4 = cancelled.
        public byte CurrentStatus { get; }
    }
}
