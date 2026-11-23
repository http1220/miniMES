using System;
using System.Collections.Generic;
using System.Linq;
using Core;

namespace Collector
{
    public sealed class ConfirmedAlarmEvent
    {
        public AlarmEventItem Item { get; }
        public DateTime OccurredAtUtc { get; }

        public ConfirmedAlarmEvent(AlarmEventItem item, DateTime occurredAtUtc)
        {
            Item = item;
            OccurredAtUtc = occurredAtUtc;
        }
    }

    public sealed class ConfirmedSample
    {
        public SampleItem Item { get; }
        public DateTime CollectedAtUtc { get; }

        public ConfirmedSample(SampleItem item, DateTime collectedAtUtc)
        {
            Item = item;
            CollectedAtUtc = collectedAtUtc;
        }
    }

    public sealed class TransmissionTimeline
    {
        private readonly object referenceGate = new object();
        private readonly ClockReference reference;
        private readonly EquipmentUtcFloor floor = new EquipmentUtcFloor();
        private readonly Dictionary<(int EquipmentId, long SequenceNumber), DateTime> sampleTimes =
            new Dictionary<(int EquipmentId, long SequenceNumber), DateTime>();
        private readonly Dictionary<long, DateTime> alarmTimes = new Dictionary<long, DateTime>();
        private DateTime? startedAtUtc;

        public TransmissionTimeline(TimeSpan clockJumpThreshold)
        {
            reference = new ClockReference(clockJumpThreshold);
        }

        public bool HasReference { get { lock (referenceGate) return reference.HasReference; } }
        public DateTime? StartedAtUtc { get { lock (referenceGate) return startedAtUtc; } }

        public ClockJump UpdateReference(TimeSpan receivedAtMonotonic, DateTime serverUtc)
        {
            lock (referenceGate)
            {
                var jump = reference.Update(receivedAtMonotonic, serverUtc);
                if (!startedAtUtc.HasValue)
                    startedAtUtc = serverUtc;
                return jump;
            }
        }

        // Call ConfirmAll and both batch methods only from the single transmission loop.
        public void ConfirmAll(CollectorState state)
        {
            if (!HasReference)
                return;

            var samples = state.GetSampleSnapshot();
            var heldAlarms = state.GetUnsentSnapshot();
            var alarms = state.GetAlarmSnapshot();
            var sampleKeys = new HashSet<(int, long)>();
            var alarmKeys = new HashSet<long>();
            var pending = new List<(int EquipmentId, TimeSpan Monotonic, AlarmEventItem Alarm, SampleItem Sample)>();

            foreach (var alarm in alarms.Concat(heldAlarms))
            {
                if (alarmKeys.Add(alarm.EventSequence) && !alarmTimes.ContainsKey(alarm.EventSequence))
                    pending.Add((alarm.EquipmentId, alarm.OccurredAt, alarm, null));
            }
            foreach (var sample in samples)
            {
                var key = (sample.EquipmentId, sample.SequenceNumber);
                sampleKeys.Add(key);
                if (!sampleTimes.ContainsKey(key))
                    pending.Add((sample.EquipmentId, sample.ReceivedAt, null, sample));
            }

            // LINQ's stable sort preserves the insertion order for ties, with alarms inserted first.
            foreach (var item in pending.OrderBy(item => item.EquipmentId).ThenBy(item => item.Monotonic))
            {
                DateTime utc;
                lock (referenceGate)
                    reference.TryToUtc(item.Monotonic, out utc);

                utc = floor.Apply(item.EquipmentId, utc);
                if (item.Alarm != null)
                    alarmTimes.Add(item.Alarm.EventSequence, utc);
                else
                    sampleTimes.Add((item.EquipmentId, item.Sample.SequenceNumber), utc);
            }

            foreach (var key in sampleTimes.Keys.Where(key => !sampleKeys.Contains(key)).ToArray())
                sampleTimes.Remove(key);
            foreach (var key in alarmTimes.Keys.Where(key => !alarmKeys.Contains(key)).ToArray())
                alarmTimes.Remove(key);
        }

        public IReadOnlyList<ConfirmedAlarmEvent> NextAlarmBatch(CollectorState state, int max)
        {
            if (max <= 0)
                throw new ArgumentOutOfRangeException(nameof(max));

            var batch = new List<ConfirmedAlarmEvent>();
            foreach (var item in state.GetAlarmSnapshot())
            {
                if (batch.Count == max || !alarmTimes.TryGetValue(item.EventSequence, out var utc))
                    break;
                batch.Add(new ConfirmedAlarmEvent(item, utc));
            }
            return batch;
        }

        public IReadOnlyList<ConfirmedSample> NextSampleBatch(CollectorState state, int max)
        {
            if (max <= 0)
                throw new ArgumentOutOfRangeException(nameof(max));

            var batch = new List<ConfirmedSample>();
            foreach (var item in state.GetSampleSnapshot())
            {
                if (batch.Count == max ||
                    !sampleTimes.TryGetValue((item.EquipmentId, item.SequenceNumber), out var utc))
                    break;
                batch.Add(new ConfirmedSample(item, utc));
            }
            return batch;
        }
    }
}
