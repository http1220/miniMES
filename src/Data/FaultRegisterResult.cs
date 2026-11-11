namespace Data
{
    public enum FaultRegisterStatus
    {
        Success,
        AlarmNotFound,
        InvalidCauseCategory,
        AlreadyRegistered
    }

    public sealed class FaultRegisterResult
    {
        internal FaultRegisterResult(FaultRegisterStatus status, int? faultId = null, byte[] version = null)
        {
            Status = status;
            FaultId = faultId;
            Version = version;
        }

        public FaultRegisterStatus Status { get; }
        public int? FaultId { get; }
        public byte[] Version { get; }
    }
}
