using System;
using System.Collections.Generic;
using Core;

namespace Collector
{
    public sealed class CollectorState
    {
        private readonly object gate = new object();
        private readonly Dictionary<int, EquipmentState> equipment = new Dictionary<int, EquipmentState>();
        private readonly BoundedSampleBuffer<SampleItem> samples;
        private readonly RejectingAlarmQueue<AlarmEventItem> alarms;
        private readonly UnsentAlarmEvents unsent = new UnsentAlarmEvents();
        private readonly int sampleCapacity;
        private readonly int alarmCapacity;
        private long eventSequence;
        private int sampleCount;
        private int alarmCount;

        public Guid RunId { get; }

        public CollectorState(Guid runId, IEnumerable<int> equipmentIds, int sampleCapacity, int alarmCapacity,
            int communicationRecoverySuccessCount, int thresholdConsecutiveCount)
        {
            if (communicationRecoverySuccessCount < 1)
                throw new ArgumentOutOfRangeException(nameof(communicationRecoverySuccessCount));
            if (thresholdConsecutiveCount < 1)
                throw new ArgumentOutOfRangeException(nameof(thresholdConsecutiveCount));

            RunId = runId;
            this.sampleCapacity = sampleCapacity;
            this.alarmCapacity = alarmCapacity;
            samples = new BoundedSampleBuffer<SampleItem>(sampleCapacity);
            alarms = new RejectingAlarmQueue<AlarmEventItem>(alarmCapacity);
            foreach (var id in equipmentIds)
                equipment.Add(id, new EquipmentState(communicationRecoverySuccessCount, thresholdConsecutiveCount));
        }

        public SampleItem AddSample(int equipmentId, TimeSpan receivedAt, float? temperature,
            float? vibration, byte? statusCode)
        {
            lock (gate)
            {
                var state = equipment[equipmentId];
                var item = new SampleItem(equipmentId, checked(++state.SampleSequence), receivedAt,
                    temperature, vibration, statusCode);
                samples.Add(item);
                if (sampleCount < sampleCapacity)
                    sampleCount++;
                return item;
            }
        }

        public AlarmEvent OnValidResponse(int equipmentId, TimeSpan now)
        {
            lock (gate)
                return equipment[equipmentId].Communication.OnValidResponse(now);
        }

        public void OnFailedResponse(int equipmentId)
        {
            lock (gate)
                equipment[equipmentId].Communication.OnFailedResponse();
        }

        public AlarmEvent CheckCommunicationTimeout(int equipmentId, TimeSpan now, TimeSpan timeout)
        {
            lock (gate)
                return equipment[equipmentId].Communication.CheckTimeout(now, timeout);
        }

        // Detection and submission happen under one lock hold so a clear can never overtake its raise.
        public SubmitResult OnValidResponseAndSubmit(int equipmentId, TimeSpan now)
        {
            lock (gate)
                return SubmitAlarm(equipmentId, AlarmCause.Communication,
                    equipment[equipmentId].Communication.OnValidResponse(now), now);
        }

        public SubmitResult CheckCommunicationTimeoutAndSubmit(int equipmentId, TimeSpan now, TimeSpan timeout)
        {
            lock (gate)
                return SubmitAlarm(equipmentId, AlarmCause.Communication,
                    equipment[equipmentId].Communication.CheckTimeout(now, timeout), now);
        }

        public AlarmEvent UpdateTemperature(int equipmentId, float value, float upperLimit)
        {
            lock (gate)
                return equipment[equipmentId].Temperature.Update(value, upperLimit);
        }

        public AlarmEvent UpdateVibration(int equipmentId, float value, float upperLimit)
        {
            lock (gate)
                return equipment[equipmentId].Vibration.Update(value, upperLimit);
        }

        public SubmitResult SubmitAlarm(int equipmentId, AlarmCause cause, AlarmEvent kind, TimeSpan occurredAt)
        {
            lock (gate)
            {
                switch (kind)
                {
                    case AlarmEvent.None:
                        return new SubmitResult(null, SubmitOutcome.Ignored, alarms.RejectedCount);
                    case AlarmEvent.Raised:
                        return Submit(equipmentId, cause, AlarmEventType.Raised, occurredAt);
                    case AlarmEvent.Cleared:
                        if (unsent.TryCancelRaised((equipmentId, cause)))
                            return new SubmitResult(null, SubmitOutcome.CancelledUnsentPair, alarms.RejectedCount);
                        return Submit(equipmentId, cause, AlarmEventType.Cleared, occurredAt);
                    default:
                        throw new ArgumentOutOfRangeException(nameof(kind));
                }
            }
        }

        public SubmitResult SubmitStatusReport(int equipmentId, AlarmCause cause, bool abnormal, TimeSpan occurredAt)
        {
            lock (gate)
                return Submit(equipmentId, cause, abnormal ? AlarmEventType.ReportAbnormal : AlarmEventType.ReportNormal,
                    occurredAt);
        }

        private SubmitResult Submit(int equipmentId, AlarmCause cause, AlarmEventType type, TimeSpan occurredAt)
        {
            var item = new AlarmEventItem(checked(++eventSequence), equipmentId, cause, type, occurredAt);
            if (!unsent.HasEvents((equipmentId, cause)) && alarms.TryAdd(item))
            {
                alarmCount++;
                return new SubmitResult(item, SubmitOutcome.Queued, alarms.RejectedCount);
            }
            unsent.Hold(item);
            return new SubmitResult(item, SubmitOutcome.HeldUnsent, alarms.RejectedCount);
        }

        public int RequeueUnsent()
        {
            lock (gate)
            {
                // Retry only while a slot is free so repeated retries do not inflate the rejected count.
                var moved = unsent.Requeue(alarms, alarmCapacity - alarmCount);
                alarmCount += moved;
                return moved;
            }
        }

        public long LastSampleSequence(int equipmentId)
        {
            lock (gate)
                return equipment[equipmentId].SampleSequence;
        }

        public SampleItem[] GetSampleSnapshot()
        {
            lock (gate)
                return samples.GetSnapshot();
        }

        public AlarmEventItem[] GetAlarmSnapshot()
        {
            lock (gate)
                return alarms.GetSnapshot();
        }

        public AlarmEventItem[] GetUnsentSnapshot()
        {
            lock (gate)
                return unsent.GetSnapshot();
        }

        public int RemoveSamples(ISet<(int EquipmentId, long SequenceNumber)> keys)
        {
            var removalKeys = new HashSet<(int EquipmentId, long SequenceNumber)>(keys);
            lock (gate)
            {
                var removed = samples.RemoveWhere(item => removalKeys.Contains((item.EquipmentId, item.SequenceNumber)));
                sampleCount -= removed;
                return removed;
            }
        }

        public int RemoveAlarmEvents(ISet<long> eventSequences)
        {
            var removalKeys = new HashSet<long>(eventSequences);
            lock (gate)
            {
                var removed = alarms.RemoveWhere(item => removalKeys.Contains(item.EventSequence));
                alarmCount -= removed;
                return removed;
            }
        }

        public long SamplesDropped { get { lock (gate) return samples.DroppedCount; } }
        public long AlarmsRejected { get { lock (gate) return alarms.RejectedCount; } }
        public int SampleCount { get { lock (gate) return sampleCount; } }
        public int AlarmCount { get { lock (gate) return alarmCount; } }
        public int UnsentAlarmCount { get { lock (gate) return unsent.Count; } }

        private sealed class EquipmentState
        {
            internal long SampleSequence;
            internal readonly CommunicationAlarmDetector Communication;
            internal readonly ThresholdAlarmDetector Temperature;
            internal readonly ThresholdAlarmDetector Vibration;

            internal EquipmentState(int recoveryCount, int thresholdCount)
            {
                Communication = new CommunicationAlarmDetector(recoveryCount);
                Temperature = new ThresholdAlarmDetector(thresholdCount);
                Vibration = new ThresholdAlarmDetector(thresholdCount);
            }
        }
    }
}
