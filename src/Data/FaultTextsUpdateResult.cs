namespace Data
{
    public enum FaultTextsUpdateStatus
    {
        Success,
        InvalidCauseCategory,
        FaultClosed,
        Conflict,
        NotFound
    }

    public sealed class FaultTextsUpdateResult
    {
        internal FaultTextsUpdateResult(FaultTextsUpdateStatus status, byte causeCategory = 0,
            string causeText = null, string actionText = null, byte[] version = null, byte currentStatus = 0)
        {
            Status = status;
            CauseCategory = causeCategory;
            CauseText = causeText;
            ActionText = actionText;
            Version = version;
            CurrentStatus = currentStatus;
        }

        public FaultTextsUpdateStatus Status { get; }
        public byte CauseCategory { get; }
        public string CauseText { get; }
        public string ActionText { get; }
        public byte[] Version { get; }
        public byte CurrentStatus { get; }
    }
}
