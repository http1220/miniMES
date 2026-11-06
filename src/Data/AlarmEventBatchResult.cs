using System;
using System.Collections.Generic;

namespace Data
{
    public sealed class AlarmEventMismatch
    {
        internal AlarmEventMismatch(AlarmEventRecord item)
        {
            RunId = item.RunId;
            EventSequence = item.EventSequence;
            EquipmentId = item.EquipmentId;
            Cause = item.Cause;
            EventType = item.EventType;
        }

        public Guid RunId { get; }
        public long EventSequence { get; }
        public int EquipmentId { get; }
        public byte Cause { get; }
        public byte EventType { get; }
    }

    public sealed class AlarmEventBatchResult
    {
        internal AlarmEventBatchResult(int appliedCount, int duplicateCount,
            List<AlarmEventMismatch> mismatches)
        {
            AppliedCount = appliedCount;
            DuplicateCount = duplicateCount;
            Mismatches = mismatches.AsReadOnly();
        }

        public int AppliedCount { get; }
        public int DuplicateCount { get; }
        public IReadOnlyList<AlarmEventMismatch> Mismatches { get; }
    }
}
