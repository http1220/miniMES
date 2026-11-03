namespace Data
{
    public enum AlarmAcknowledgementStatus
    {
        Success,
        AlreadyAcknowledged
    }

    public sealed class AlarmAcknowledgementResult
    {
        internal AlarmAcknowledgementResult(AlarmAcknowledgementStatus status, int? acknowledgedBy)
        {
            Status = status;
            AcknowledgedBy = acknowledgedBy;
        }

        public AlarmAcknowledgementStatus Status { get; }

        public int? AcknowledgedBy { get; }
    }
}
