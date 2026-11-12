namespace Data
{
    public enum FaultTransitionStatus
    {
        Success,
        StatusMismatch,
        NotFound,
        ActiveFaultExists
    }

    public sealed class FaultTransitionResult
    {
        internal FaultTransitionResult(FaultTransitionStatus status, byte currentStatus = 0, int? faultId = null, byte[] version = null)
        {
            Status = status;
            CurrentStatus = currentStatus;
            FaultId = faultId;
            Version = status == FaultTransitionStatus.Success ? version : null;
        }

        public FaultTransitionStatus Status { get; }
        public byte CurrentStatus { get; }
        public int? FaultId { get; }
        public byte[] Version { get; }
    }
}
