using System;
using System.Collections.Generic;
using System.Linq;
using Collector;
using Core;
using Xunit;

namespace UnitTests
{
    public class CollectorFirstReportTests
    {
        [Fact]
        public void SubmitAlarm_FirstRaiseReportsAbnormal_ThenClearAndRaiseAreTransitions()
        {
            var state = CreateState();
            var at = TimeSpan.FromSeconds(2);
            var report = state.SubmitAlarm(1, AlarmCause.Temperature, AlarmEvent.Raised, at);
            var clear = state.SubmitAlarm(1, AlarmCause.Temperature, AlarmEvent.Cleared, at);
            var raise = state.SubmitAlarm(1, AlarmCause.Temperature, AlarmEvent.Raised, at);

            Assert.Equal(SubmitOutcome.Queued, report.Outcome);
            Assert.Equal(AlarmEventType.ReportAbnormal, report.Item.EventType);
            Assert.Equal(1L, report.Item.EventSequence);
            Assert.Equal(at, report.Item.OccurredAt);
            Assert.Equal(AlarmEventType.Cleared, clear.Item.EventType);
            Assert.Equal(AlarmEventType.Raised, raise.Item.EventType);
            Assert.Equal(new[] { report.Item, clear.Item, raise.Item }, state.GetAlarmSnapshot());
        }

        [Theory]
        [InlineData(AlarmCause.Temperature)]
        [InlineData(AlarmCause.Vibration)]
        public void UpdateThreshold_FirstNormalReport_RequiresThreeConsecutiveValues(AlarmCause cause)
        {
            var state = CreateState();
            SubmitResult Update(float value, int seconds)
            {
                var at = TimeSpan.FromSeconds(seconds);
                return cause == AlarmCause.Temperature
                    ? state.UpdateTemperatureAndSubmit(1, value, 10, at)
                    : state.UpdateVibrationAndSubmit(1, value, 10, at);
            }

            Assert.Equal(SubmitOutcome.Ignored, Update(10, 1).Outcome);
            Assert.Equal(SubmitOutcome.Ignored, Update(10, 2).Outcome);
            Assert.Equal(SubmitOutcome.Ignored, Update(11, 3).Outcome);
            Assert.Equal(SubmitOutcome.Ignored, Update(10, 4).Outcome);
            Assert.Equal(SubmitOutcome.Ignored, Update(9, 5).Outcome);
            var report = Update(10, 6);
            Assert.Equal(SubmitOutcome.Queued, report.Outcome);
            Assert.Equal(AlarmEventType.ReportNormal, report.Item.EventType);
            Assert.Equal(cause, report.Item.Cause);
            Assert.Equal(TimeSpan.FromSeconds(6), report.Item.OccurredAt);
            Assert.Equal(SubmitOutcome.Ignored, Update(10, 7).Outcome);
            Assert.Same(report.Item, Assert.Single(state.GetAlarmSnapshot()));

            Assert.Equal(SubmitOutcome.Ignored, Update(11, 8).Outcome);
            Assert.Equal(SubmitOutcome.Ignored, Update(11, 9).Outcome);
            Assert.Equal(AlarmEventType.Raised, Update(11, 10).Item.EventType);
        }

        [Fact]
        public void Communication_StartupTimeoutReportsAbnormal_ThenRecoveryClears()
        {
            var start = TimeSpan.FromSeconds(10);
            var state = CreateState(start);
            var timeout = TimeSpan.FromSeconds(5);
            var deadline = start + timeout;
            Assert.Equal(SubmitOutcome.Ignored,
                state.CheckCommunicationTimeoutAndSubmit(1, deadline - TimeSpan.FromTicks(1), timeout).Outcome);

            var report = state.CheckCommunicationTimeoutAndSubmit(1, deadline, timeout);
            Assert.Equal(SubmitOutcome.Queued, report.Outcome);
            Assert.Equal(AlarmEventType.ReportAbnormal, report.Item.EventType);
            Assert.Equal(deadline, report.Item.OccurredAt);
            Assert.Equal(SubmitOutcome.Ignored,
                state.CheckCommunicationTimeoutAndSubmit(1, deadline + TimeSpan.FromTicks(1), timeout).Outcome);
            Assert.Equal(SubmitOutcome.Ignored, state.OnValidResponseAndSubmit(1, deadline + TimeSpan.FromSeconds(1)).Outcome);
            Assert.Equal(SubmitOutcome.Ignored, state.OnValidResponseAndSubmit(1, deadline + TimeSpan.FromSeconds(2)).Outcome);
            var cleared = state.OnValidResponseAndSubmit(1, deadline + TimeSpan.FromSeconds(3));
            Assert.Equal(AlarmEventType.Cleared, cleared.Item.EventType);
            Assert.Equal(new[] { report.Item, cleared.Item }, state.GetAlarmSnapshot());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Communication_FirstNormalReport_RequiresThreeSuccessesWithoutFailure(bool interrupt)
        {
            var state = CreateState(TimeSpan.Zero);
            var receivedAt = TimeSpan.Zero;
            var interval = TimeSpan.FromMilliseconds(100);
            if (interrupt)
            {
                Assert.Equal(SubmitOutcome.Ignored, state.OnValidResponseAndSubmit(1, receivedAt).Outcome);
                receivedAt += interval;
                Assert.Equal(SubmitOutcome.Ignored, state.OnValidResponseAndSubmit(1, receivedAt).Outcome);
                state.OnFailedResponse(1);
                receivedAt += interval;
            }

            Assert.Equal(SubmitOutcome.Ignored, state.OnValidResponseAndSubmit(1, receivedAt).Outcome);
            receivedAt += interval;
            Assert.Equal(SubmitOutcome.Ignored, state.OnValidResponseAndSubmit(1, receivedAt).Outcome);
            receivedAt += interval;
            var report = state.OnValidResponseAndSubmit(1, receivedAt);
            Assert.Equal(SubmitOutcome.Queued, report.Outcome);
            Assert.Equal(AlarmEventType.ReportNormal, report.Item.EventType);
            Assert.Equal(receivedAt, report.Item.OccurredAt);
            Assert.Equal(SubmitOutcome.Ignored, state.OnValidResponseAndSubmit(1, receivedAt + interval).Outcome);
            Assert.Same(report.Item, Assert.Single(state.GetAlarmSnapshot()));
        }

        [Fact]
        public void FirstReport_EquipmentAndCauseHaveIndependentCountersAndGuards()
        {
            var state = CreateState();
            var at = TimeSpan.Zero;
            for (int i = 0; i < 2; i++)
            {
                Assert.Equal(SubmitOutcome.Ignored, state.UpdateTemperatureAndSubmit(1, 10, 10, at).Outcome);
                Assert.Equal(SubmitOutcome.Ignored, state.UpdateVibrationAndSubmit(1, 10, 10, at).Outcome);
                Assert.Equal(SubmitOutcome.Ignored, state.UpdateTemperatureAndSubmit(2, 10, 10, at).Outcome);
            }

            Assert.Equal(AlarmEventType.ReportNormal, state.UpdateTemperatureAndSubmit(1, 10, 10, at).Item.EventType);
            Assert.Equal(AlarmEventType.ReportNormal, state.UpdateVibrationAndSubmit(1, 10, 10, at).Item.EventType);
            Assert.Equal(AlarmEventType.ReportNormal, state.UpdateTemperatureAndSubmit(2, 10, 10, at).Item.EventType);
            var duplicate = state.SubmitStatusReport(1, AlarmCause.Temperature, true, at);
            Assert.Equal(SubmitOutcome.Ignored, duplicate.Outcome);
            Assert.Null(duplicate.Item);
            Assert.Equal(3, state.AlarmCount);
        }

        [Fact]
        public void FullQueue_RepeatedTransitionsAndReports_KeepAtMostThreeUnsentPerKey()
        {
            var state = new CollectorState(Guid.Empty, Enumerable.Range(1, 5), 8, 1, 3, 3);
            var at = TimeSpan.Zero;
            var queued = state.SubmitAlarm(5, AlarmCause.Vibration, AlarmEvent.Raised, at);
            var causes = new[] { AlarmCause.Communication, AlarmCause.Temperature, AlarmCause.Vibration };

            for (var id = 1; id <= 5; id++)
            foreach (var cause in causes)
            {
                var alreadyReported = id == 5 && cause == AlarmCause.Vibration;
                if (alreadyReported)
                    state.SubmitAlarm(id, cause, AlarmEvent.Cleared, at);

                for (var cycle = 0; cycle < 20; cycle++)
                {
                    var raised = state.SubmitAlarm(id, cause, AlarmEvent.Raised, at);
                    Assert.Equal(SubmitOutcome.HeldUnsent, raised.Outcome);
                    Assert.Equal(cycle == 0 && !alreadyReported ? AlarmEventType.ReportAbnormal : AlarmEventType.Raised,
                        raised.Item.EventType);
                    var duplicate = state.SubmitStatusReport(id, cause, cycle % 2 == 0, at);
                    Assert.Equal(SubmitOutcome.Ignored, duplicate.Outcome);
                    Assert.Null(duplicate.Item);
                    var cleared = state.SubmitAlarm(id, cause, AlarmEvent.Cleared, at);
                    Assert.Equal(cycle == 0 && !alreadyReported ? SubmitOutcome.HeldUnsent : SubmitOutcome.CancelledUnsentPair,
                        cleared.Outcome);
                }
                Assert.Equal(SubmitOutcome.HeldUnsent, state.SubmitAlarm(id, cause, AlarmEvent.Raised, at).Outcome);
            }

            var held = state.GetUnsentSnapshot();
            Assert.Equal(44, held.Length);
            Assert.InRange(held.Length, 1, 45);
            Assert.Equal(15, held.Select(item => (item.EquipmentId, item.Cause)).Distinct().Count());
            Assert.All(held.GroupBy(item => (item.EquipmentId, item.Cause)), key =>
            {
                var expected = key.Key == (5, AlarmCause.Vibration)
                    ? new[] { AlarmEventType.Cleared, AlarmEventType.Raised }
                    : new[] { AlarmEventType.ReportAbnormal, AlarmEventType.Cleared, AlarmEventType.Raised };
                Assert.Equal(expected, key.Select(item => item.EventType));
            });

            Assert.Same(queued.Item, Assert.Single(state.GetAlarmSnapshot()));
            Assert.Equal(0, state.RequeueUnsent());
            var previous = queued.Item;
            foreach (var expected in held)
            {
                Assert.Equal(1, state.RemoveAlarmEvents(new HashSet<long> { previous.EventSequence }));
                Assert.Equal(1, state.RequeueUnsent());
                previous = Assert.Single(state.GetAlarmSnapshot());
                Assert.Same(expected, previous);
            }
            Assert.Empty(state.GetUnsentSnapshot());
        }

        private static CollectorState CreateState(TimeSpan? communicationStartTime = null)
        {
            return communicationStartTime.HasValue
                ? new CollectorState(Guid.Empty, new[] { 1, 2 }, 8, 8, 3, 3, communicationStartTime.Value)
                : new CollectorState(Guid.Empty, new[] { 1, 2 }, 8, 8, 3, 3);
        }
    }
}
