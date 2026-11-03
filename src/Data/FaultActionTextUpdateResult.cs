namespace Data
{
    public enum FaultActionTextUpdateStatus
    {
        Success,
        Conflict
    }

    public sealed class FaultActionTextUpdateResult
    {
        internal FaultActionTextUpdateResult(FaultActionTextUpdateStatus status, string actionText, byte[] version)
        {
            Status = status;
            ActionText = actionText;
            Version = version;
        }

        public FaultActionTextUpdateStatus Status { get; }

        // Saved text on success; current database text on conflict. May be null.
        public string ActionText { get; }

        // New version on success; current database version on conflict.
        public byte[] Version { get; }
    }
}
