using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Collector;
using Core;
using Xunit;

namespace UnitTests
{
    public class CollectorStateTests
    {
        [Fact]
        public void AddSample_InterleavedEquipment_SequencesIndependentlyAndPreservesValues()
        {
            var state = CreateState();
            var at = TimeSpan.FromMilliseconds(42);
            var first = state.AddSample(1, at, 2f, null, 3);

            Assert.Equal(1L, first.SequenceNumber);
            Assert.Equal(1L, state.AddSample(2, at, null, null, null).SequenceNumber);
            Assert.Equal(2L, state.AddSample(1, at, null, null, null).SequenceNumber);
            Assert.Equal(at, first.ReceivedAt);
            Assert.Equal((float?)2f, first.Temperature);
            Assert.Null(first.Vibration);
            Assert.Equal((byte?)3, first.StatusCode);
        }

        [Fact]
        public void RemoveSamples_KeysFromBeforeOverflow_RemovesOnlyMatchingRemainingSamples()
        {
            var state = CreateState(sampleCapacity: 2);
            state.AddSample(1, TimeSpan.Zero, null, null, null);
            state.AddSample(2, TimeSpan.Zero, null, null, null);
            var sent = state.GetSampleSnapshot();
            var latest = state.AddSample(1, TimeSpan.Zero, null, null, null);

            Assert.Equal(1L, state.SamplesDropped);
            Assert.Equal(2, state.SampleCount);
            Assert.Equal(new[] { sent[1], latest }, state.GetSampleSnapshot());
            var keys = new HashSet<(int, long)>(sent.Select(item => (item.EquipmentId, item.SequenceNumber)));
            Assert.Equal(1, state.RemoveSamples(keys));
            Assert.Equal(0, state.RemoveSamples(keys));
            Assert.Equal(1, state.SampleCount);
            Assert.Same(latest, Assert.Single(state.GetSampleSnapshot()));
            Assert.Equal(1L, state.SamplesDropped);
        }

        [Fact]
        public void RequeueUnsent_FullQueueThenRemoval_PreservesOriginalEvent()
        {
            var state = CreateState(alarmCapacity: 1);
            var queued = state.SubmitStatusReport(2, AlarmCause.Communication, false, TimeSpan.Zero);
            var at = TimeSpan.FromSeconds(3);
            var held = state.SubmitAlarm(1, AlarmCause.Temperature, AlarmEvent.Raised, at);

            Assert.Equal(SubmitOutcome.Queued, queued.Outcome);
            Assert.Equal(AlarmEventType.ReportNormal, queued.Item.EventType);
            Assert.Equal(1L, queued.Item.EventSequence);
            Assert.Equal(SubmitOutcome.HeldUnsent, held.Outcome);
            Assert.Equal(2L, held.Item.EventSequence);
            Assert.Equal(1L, held.AlarmsRejectedTotal);
            Assert.Equal(1, state.UnsentAlarmCount);
            Assert.Equal(0, state.RemoveAlarmEvents(new HashSet<long> { held.Item.EventSequence }));
            Assert.Equal(1, state.RemoveAlarmEvents(new HashSet<long> { queued.Item.EventSequence }));
            Assert.Equal(1, state.RequeueUnsent());
            Assert.Equal(0, state.UnsentAlarmCount);
            Assert.Equal(1, state.AlarmCount);
            Assert.Equal(1L, state.AlarmsRejected);
            var restored = Assert.Single(state.GetAlarmSnapshot());
            Assert.Same(held.Item, restored);
            Assert.Equal(at, restored.OccurredAt);
        }

        [Fact]
        public void SubmitAlarm_UnsentRaisedFollowedByCleared_CancelsWithoutAllocatingSequence()
        {
            var state = CreateState(alarmCapacity: 1);
            var first = state.SubmitStatusReport(2, AlarmCause.Communication, false, TimeSpan.Zero);
            var report = state.SubmitStatusReport(1, AlarmCause.Temperature, false, TimeSpan.Zero);
            Assert.Equal(SubmitOutcome.HeldUnsent, report.Outcome);
            var raised = state.SubmitAlarm(1, AlarmCause.Temperature, AlarmEvent.Raised, TimeSpan.Zero);
            var cleared = state.SubmitAlarm(1, AlarmCause.Temperature, AlarmEvent.Cleared, TimeSpan.Zero);
            var ignored = state.SubmitAlarm(1, AlarmCause.Temperature, AlarmEvent.None, TimeSpan.Zero);

            Assert.Equal(SubmitOutcome.CancelledUnsentPair, cleared.Outcome);
            Assert.Null(cleared.Item);
            Assert.Equal(SubmitOutcome.Ignored, ignored.Outcome);
            Assert.Null(ignored.Item);
            Assert.Same(report.Item, Assert.Single(state.GetUnsentSnapshot()));
            Assert.Equal(0, state.RequeueUnsent());
            state.RemoveAlarmEvents(new HashSet<long> { first.Item.EventSequence });
            Assert.Equal(1, state.RequeueUnsent());
            var next = state.SubmitAlarm(1, AlarmCause.Temperature, AlarmEvent.Raised, TimeSpan.Zero);
            Assert.Equal(raised.Item.EventSequence + 1, next.Item.EventSequence);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SubmitAlarm_EarlierSameKeyReportIsUnsent_PreservesOrderAndAllowsOtherKey(bool abnormal)
        {
            var state = CreateState(alarmCapacity: 3);
            var fill = Enumerable.Range(0, 3)
                .Select(_ => state.SubmitAlarm(2, AlarmCause.Communication, AlarmEvent.Raised, TimeSpan.Zero).Item.EventSequence)
                .ToArray();

            var report = state.SubmitStatusReport(1, AlarmCause.Temperature, abnormal, TimeSpan.Zero);
            Assert.Equal(SubmitOutcome.HeldUnsent, report.Outcome);
            Assert.Equal(abnormal ? AlarmEventType.ReportAbnormal : AlarmEventType.ReportNormal, report.Item.EventType);
            state.RemoveAlarmEvents(new HashSet<long>(fill));

            var expected = new List<AlarmEventItem> { report.Item };
            if (abnormal)
            {
                var clear = state.SubmitAlarm(1, AlarmCause.Temperature, AlarmEvent.Cleared, TimeSpan.Zero);
                Assert.Equal(SubmitOutcome.HeldUnsent, clear.Outcome);
                expected.Add(clear.Item);
            }
            var raised = state.SubmitAlarm(1, AlarmCause.Temperature, AlarmEvent.Raised, TimeSpan.Zero);
            Assert.Equal(SubmitOutcome.HeldUnsent, raised.Outcome);
            expected.Add(raised.Item);
            Assert.Empty(state.GetAlarmSnapshot());
            Assert.Equal(1L, state.AlarmsRejected);

            var other = state.SubmitStatusReport(1, AlarmCause.Vibration, false, TimeSpan.Zero);
            Assert.Equal(SubmitOutcome.Queued, other.Outcome);
            state.RemoveAlarmEvents(new HashSet<long> { other.Item.EventSequence });
            Assert.Equal(expected.Count, state.RequeueUnsent());
            Assert.Equal(expected, state.GetAlarmSnapshot());
        }

        [Fact]
        public void RequeueUnsent_MultipleKeysAndOneOpening_VisitsKeysInOrderWithoutInflatingRejections()
        {
            var state = CreateState(alarmCapacity: 1);
            var first = state.SubmitStatusReport(1, AlarmCause.Communication, false, TimeSpan.Zero);
            var secondKey = state.SubmitStatusReport(2, AlarmCause.Communication, false, TimeSpan.Zero);
            var firstKey = state.SubmitStatusReport(1, AlarmCause.Vibration, false, TimeSpan.Zero);
            Assert.Equal(0, state.RequeueUnsent());
            Assert.Equal(0, state.RequeueUnsent());
            Assert.Equal(2L, state.AlarmsRejected);
            state.RemoveAlarmEvents(new HashSet<long> { first.Item.EventSequence });

            Assert.Equal(1, state.RequeueUnsent());
            Assert.Same(firstKey.Item, Assert.Single(state.GetAlarmSnapshot()));
            Assert.Equal(2L, state.AlarmsRejected);
            state.RemoveAlarmEvents(new HashSet<long> { firstKey.Item.EventSequence });
            Assert.Equal(1, state.RequeueUnsent());
            Assert.Same(secondKey.Item, Assert.Single(state.GetAlarmSnapshot()));
        }

        [Fact]
        public void Update_ConfiguredDetectors_UseTwoConsecutiveValuesPerEquipmentAndCause()
        {
            var state = CreateState();
            var timeout = TimeSpan.FromSeconds(5);
            Assert.Equal(AlarmEvent.None, state.OnValidResponse(1, TimeSpan.Zero));
            Assert.Equal(AlarmEvent.Raised, state.CheckCommunicationTimeout(1, timeout, timeout));
            Assert.Equal(AlarmEvent.None, state.OnValidResponse(1, timeout));
            state.OnFailedResponse(1);
            Assert.Equal(AlarmEvent.None, state.OnValidResponse(1, timeout));
            Assert.Equal(AlarmEvent.Cleared, state.OnValidResponse(1, timeout));

            Assert.Equal(AlarmEvent.None, state.UpdateTemperature(1, 81, 80));
            Assert.Equal(AlarmEvent.None, state.UpdateTemperature(2, 81, 80));
            Assert.Equal(AlarmEvent.None, state.UpdateVibration(1, 11, 10));
            Assert.Equal(AlarmEvent.Raised, state.UpdateTemperature(1, 81, 80));
            Assert.Equal(AlarmEvent.Raised, state.UpdateVibration(1, 11, 10));
            Assert.Equal(AlarmEvent.None, state.UpdateTemperature(1, 80, 80));
            Assert.Equal(AlarmEvent.Cleared, state.UpdateTemperature(1, 80, 80));
            Assert.Equal(AlarmEvent.None, state.UpdateVibration(1, 10, 10));
            Assert.Equal(AlarmEvent.Cleared, state.UpdateVibration(1, 10, 10));
        }

        [Fact]
        public void AddSample_ConcurrentSubmissions_AllocateUniqueSampleAndEventSequences()
        {
            const int count = 400;
            var state = CreateState(count, count);
            Assert.Equal(Guid.Empty, state.RunId);
            Parallel.For(0, count, i =>
            {
                var id = 1 + i % 2;
                state.AddSample(id, TimeSpan.Zero, null, null, null);
                state.SubmitAlarm(id, AlarmCause.Temperature, AlarmEvent.Raised, TimeSpan.Zero);
            });

            var samples = state.GetSampleSnapshot();
            Assert.Equal(count, samples.Length);
            foreach (var id in new[] { 1, 2 })
                Assert.Equal(Enumerable.Range(1, count / 2).Select(i => (long)i),
                    samples.Where(item => item.EquipmentId == id).Select(item => item.SequenceNumber));
            Assert.Equal(2, state.GetAlarmSnapshot().Count(item => item.EventType == AlarmEventType.ReportAbnormal));
            Assert.Equal(Enumerable.Range(1, count).Select(i => (long)i),
                state.GetAlarmSnapshot().Select(item => item.EventSequence));
        }

        [Fact]
        public void CommunicationAlarm_ConcurrentDetectionAndRecovery_KeepsRaisedBeforeCleared()
        {
            var state = new CollectorState(Guid.Empty, new[] { 1 }, 8, 100000, 1, 2);
            var timeout = TimeSpan.FromSeconds(5);
            state.OnValidResponseAndSubmit(1, TimeSpan.Zero);

            var judge = Task.Run(() =>
            {
                for (var i = 0; i < 20000; i++)
                    state.CheckCommunicationTimeoutAndSubmit(1, timeout, timeout);
            });
            var equipment = Task.Run(() =>
            {
                for (var i = 0; i < 20000; i++)
                    state.OnValidResponseAndSubmit(1, TimeSpan.Zero);
            });
            Task.WaitAll(judge, equipment);

            var types = state.GetAlarmSnapshot().Select(item => item.EventType).ToArray();
            Assert.NotEmpty(types);
            Assert.Equal(AlarmEventType.ReportNormal, types[0]);
            Assert.True(types.Length > 1);
            for (var i = 1; i < types.Length; i++)
                Assert.Equal(i % 2 == 1 ? AlarmEventType.Raised : AlarmEventType.Cleared, types[i]);
        }

        [Fact]
        public void CheckCommunicationTimeoutAndSubmit_ThenRecovery_QueuesAbnormalReportThenCleared()
        {
            var state = CreateState();
            var timeout = TimeSpan.FromSeconds(5);
            Assert.Equal(SubmitOutcome.Ignored, state.OnValidResponseAndSubmit(1, TimeSpan.Zero).Outcome);

            var raised = state.CheckCommunicationTimeoutAndSubmit(1, timeout, timeout);
            Assert.Equal(SubmitOutcome.Queued, raised.Outcome);
            Assert.Equal(AlarmEventType.ReportAbnormal, raised.Item.EventType);
            Assert.Equal(SubmitOutcome.Ignored, state.CheckCommunicationTimeoutAndSubmit(1, timeout, timeout).Outcome);
            Assert.Equal(SubmitOutcome.Ignored, state.OnValidResponseAndSubmit(1, timeout).Outcome);
            var cleared = state.OnValidResponseAndSubmit(1, timeout);
            Assert.Equal(AlarmEventType.Cleared, cleared.Item.EventType);
            Assert.Equal(raised.Item.EventSequence + 1, cleared.Item.EventSequence);
        }

        private static CollectorState CreateState(int sampleCapacity = 8, int alarmCapacity = 8)
        {
            return new CollectorState(Guid.Empty, new[] { 1, 2 }, sampleCapacity, alarmCapacity, 2, 2);
        }
    }
}
