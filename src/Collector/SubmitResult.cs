namespace Collector
{
    public enum SubmitOutcome { Queued, HeldUnsent, CancelledUnsentPair, Ignored }

    public sealed class SubmitResult
    {
        public AlarmEventItem Item { get; }
        public SubmitOutcome Outcome { get; }
        public long AlarmsRejectedTotal { get; }

        internal SubmitResult(AlarmEventItem item, SubmitOutcome outcome, long alarmsRejectedTotal)
        {
            Item = item;
            Outcome = outcome;
            AlarmsRejectedTotal = alarmsRejectedTotal;
        }
    }
}
