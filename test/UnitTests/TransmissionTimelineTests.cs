using System;
using System.Collections.Generic;
using System.Linq;
using Collector;
using Core;
using Xunit;

namespace UnitTests
{
    public class TransmissionTimelineTests
    {
        private static readonly DateTime Noon = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void ConfirmAll_WithoutReference_LeavesBatchesEmpty()
        {
            var state = CreateState();
            var timeline = CreateTimeline();
            AddSample(state, 1, 1);
            AddAlarm(state, 1, 2);

            timeline.ConfirmAll(state);

            Assert.False(timeline.HasReference);
            Assert.Null(timeline.StartedAtUtc);
            Assert.Empty(timeline.NextSampleBatch(state, 25));
            Assert.Empty(timeline.NextAlarmBatch(state, 25));
        }

        [Fact]
        public void UpdateReference_PreservesFirstServerTimeAndIgnoresOlderResponse()
        {
            var timeline = CreateTimeline();
            var state = CreateState();

            Assert.Null(timeline.UpdateReference(Seconds(10), Noon));
            Assert.True(timeline.HasReference);
            Assert.Equal(Noon, timeline.StartedAtUtc);
            Assert.NotNull(timeline.UpdateReference(Seconds(20), Noon.AddMinutes(1)));
            Assert.Null(timeline.UpdateReference(Seconds(19), Noon.AddHours(1)));
            Assert.Equal(Noon, timeline.StartedAtUtc);

            AddSample(state, 1, 21);
            timeline.ConfirmAll(state);
            Assert.Equal(Noon.AddSeconds(61), Assert.Single(timeline.NextSampleBatch(state, 25)).CollectedAtUtc);
        }

        [Fact]
        public void ConfirmAll_ProjectsSamplesCollectedBeforeFirstReference()
        {
            var state = CreateState();
            var timeline = CreateTimeline();
            for (var second = 1; second <= 3; second++)
                AddSample(state, 1, second);

            timeline.UpdateReference(Seconds(10), Noon);
            timeline.ConfirmAll(state);

            Assert.Equal(new[] { Noon.AddSeconds(-9), Noon.AddSeconds(-8), Noon.AddSeconds(-7) },
                timeline.NextSampleBatch(state, 25).Select(sample => sample.CollectedAtUtc));
        }

        [Fact]
        public void ConfirmAll_ReferenceJumpChangesOnlyNewItems()
        {
            var state = CreateState();
            var timeline = CreateTimeline();
            timeline.UpdateReference(Seconds(10), Noon);
            AddSample(state, 1, 10);
            AddAlarm(state, 1, 11);
            timeline.ConfirmAll(state);

            timeline.UpdateReference(Seconds(20), Noon.AddMinutes(1));
            AddSample(state, 1, 21);
            timeline.ConfirmAll(state);

            Assert.Equal(new[] { Noon, Noon.AddSeconds(61) },
                timeline.NextSampleBatch(state, 25).Select(sample => sample.CollectedAtUtc));
            Assert.Equal(Noon.AddSeconds(1), Assert.Single(timeline.NextAlarmBatch(state, 25)).OccurredAtUtc);
        }

        [Fact]
        public void ConfirmAll_SamplesAndAlarmsShareOnlyTheirEquipmentFloor()
        {
            var state = CreateState();
            var timeline = CreateTimeline();
            timeline.UpdateReference(Seconds(10), Noon);
            AddSample(state, 1, 10);
            AddAlarm(state, 1, 20);
            timeline.ConfirmAll(state);

            Assert.Equal(Noon, Assert.Single(timeline.NextSampleBatch(state, 25)).CollectedAtUtc);
            var alarmUtc = Assert.Single(timeline.NextAlarmBatch(state, 25)).OccurredAtUtc;
            Assert.Equal(Noon.AddSeconds(10), alarmUtc);

            timeline.UpdateReference(Seconds(21), Noon.AddSeconds(-20));
            AddSample(state, 1, 22);
            AddSample(state, 2, 22);
            timeline.ConfirmAll(state);

            Assert.Equal(new[] { Noon, alarmUtc, Noon.AddSeconds(-19) },
                timeline.NextSampleBatch(state, 25).Select(sample => sample.CollectedAtUtc));
        }

        [Fact]
        public void ConfirmAll_HeldAlarmAdvancesFloorAndKeepsTimeWhenRequeued()
        {
            var state = CreateState(alarmCapacity: 1);
            var timeline = CreateTimeline();
            timeline.UpdateReference(Seconds(10), Noon);
            var queued = AddAlarm(state, 2, 10);
            AddSample(state, 1, 15);
            var held = AddAlarm(state, 1, 20);
            Assert.Equal(SubmitOutcome.HeldUnsent, held.Outcome);

            timeline.ConfirmAll(state);

            Assert.Equal(Noon.AddSeconds(5), Assert.Single(timeline.NextSampleBatch(state, 25)).CollectedAtUtc);
            Assert.Same(queued.Item, Assert.Single(timeline.NextAlarmBatch(state, 25)).Item);

            timeline.UpdateReference(Seconds(21), Noon.AddSeconds(-20));
            AddSample(state, 1, 22);
            timeline.ConfirmAll(state);
            Assert.Equal(Noon.AddSeconds(10), timeline.NextSampleBatch(state, 25)[1].CollectedAtUtc);

            state.RemoveAlarmEvents(new HashSet<long> { queued.Item.EventSequence });
            Assert.Equal(1, state.RequeueUnsent());
            var confirmed = Assert.Single(timeline.NextAlarmBatch(state, 25));
            Assert.Same(held.Item, confirmed.Item);
            Assert.Equal(Noon.AddSeconds(10), confirmed.OccurredAtUtc);
        }

        [Fact]
        public void NextBatches_PreserveStorageOrderLimitsAndTimesAcrossRetries()
        {
            var state = CreateState();
            var timeline = CreateTimeline();
            timeline.UpdateReference(Seconds(10), Noon);
            var olderInBuffer = AddSample(state, 1, 12);
            var laterInBuffer = AddSample(state, 1, 11);
            var firstQueued = AddAlarm(state, 2, 12).Item;
            var secondQueued = AddAlarm(state, 2, 11).Item;
            timeline.ConfirmAll(state);
            var unconfirmedSample = AddSample(state, 1, 13);
            var unconfirmedAlarm = AddAlarm(state, 2, 13).Item;

            var samples = timeline.NextSampleBatch(state, 25);
            var alarms = timeline.NextAlarmBatch(state, 25);
            Assert.Equal(new[] { olderInBuffer, laterInBuffer }, samples.Select(sample => sample.Item));
            Assert.Equal(new[] { firstQueued, secondQueued }, alarms.Select(alarm => alarm.Item));
            Assert.Same(olderInBuffer, Assert.Single(timeline.NextSampleBatch(state, 1)).Item);
            Assert.Same(firstQueued, Assert.Single(timeline.NextAlarmBatch(state, 1)).Item);
            Assert.Equal(samples.Select(sample => sample.CollectedAtUtc),
                timeline.NextSampleBatch(state, 25).Select(sample => sample.CollectedAtUtc));
            Assert.Equal(alarms.Select(alarm => alarm.OccurredAtUtc),
                timeline.NextAlarmBatch(state, 25).Select(alarm => alarm.OccurredAtUtc));
            Assert.Equal(Noon.AddSeconds(1), samples[1].CollectedAtUtc);
            Assert.Equal(Noon.AddSeconds(1), alarms[1].OccurredAtUtc);

            state.RemoveSamples(new HashSet<(int, long)>(samples.Select(s => (s.Item.EquipmentId, s.Item.SequenceNumber))));
            state.RemoveAlarmEvents(new HashSet<long>(alarms.Select(a => a.Item.EventSequence)));
            Assert.Empty(timeline.NextSampleBatch(state, 25));
            Assert.Empty(timeline.NextAlarmBatch(state, 25));
            timeline.ConfirmAll(state);
            Assert.Same(unconfirmedSample, Assert.Single(timeline.NextSampleBatch(state, 25)).Item);
            Assert.Same(unconfirmedAlarm, Assert.Single(timeline.NextAlarmBatch(state, 25)).Item);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void NextAlarmBatch_StopsAtUnconfirmedHeadEvenIfRequeuedItemIsConfirmed(int max)
        {
            var state = CreateState(alarmCapacity: 2);
            var timeline = CreateTimeline();
            var first = AddAlarm(state, 2, 1).Item;
            var second = AddAlarm(state, 2, 2).Item;
            var held = AddAlarm(state, 1, 3);
            Assert.Equal(SubmitOutcome.HeldUnsent, held.Outcome);
            timeline.UpdateReference(Seconds(10), Noon);
            timeline.ConfirmAll(state);

            state.RemoveAlarmEvents(new HashSet<long> { first.EventSequence, second.EventSequence });
            var newest = AddAlarm(state, 2, 4).Item;
            Assert.Equal(1, state.RequeueUnsent());
            Assert.Equal(new[] { newest, held.Item }, state.GetAlarmSnapshot());
            Assert.Empty(timeline.NextAlarmBatch(state, max));
        }

        [Fact]
        public void GetUnsentSnapshot_CopiesItemsInKeyAndListOrder()
        {
            var state = CreateState(alarmCapacity: 1);
            AddAlarm(state, 1, 1);
            var secondKey = AddAlarm(state, 2, 2).Item;
            var firstKeyStart = AddAlarm(state, 1, 3).Item;
            var firstKeyEnd = AddAlarm(state, 1, 4).Item;

            var snapshot = state.GetUnsentSnapshot();
            Assert.Equal(new[] { firstKeyStart, firstKeyEnd, secondKey }, snapshot);
            snapshot[0] = secondKey;
            Assert.Same(firstKeyStart, state.GetUnsentSnapshot()[0]);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void NextBatches_RejectNonpositiveLimits(int max)
        {
            var state = CreateState();
            var timeline = CreateTimeline();
            Assert.Throws<ArgumentOutOfRangeException>(() => timeline.NextSampleBatch(state, max));
            Assert.Throws<ArgumentOutOfRangeException>(() => timeline.NextAlarmBatch(state, max));
        }

        private static TransmissionTimeline CreateTimeline() => new TransmissionTimeline(Seconds(1));

        private static CollectorState CreateState(int alarmCapacity = 8)
            => new CollectorState(Guid.Empty, new[] { 1, 2 }, 8, alarmCapacity, 2, 2);

        private static SampleItem AddSample(CollectorState state, int equipmentId, int second)
            => state.AddSample(equipmentId, Seconds(second), null, null, null);

        private static SubmitResult AddAlarm(CollectorState state, int equipmentId, int second)
            => state.SubmitAlarm(equipmentId, AlarmCause.Temperature, AlarmEvent.Raised, Seconds(second));

        private static TimeSpan Seconds(int value) => TimeSpan.FromSeconds(value);
    }
}
