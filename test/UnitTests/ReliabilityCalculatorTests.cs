using System;
using System.Collections.Generic;
using System.Linq;
using Core;
using Xunit;

namespace UnitTests
{
    public class ReliabilityCalculatorTests
    {
        private static readonly DateTime From = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime To = From.AddMinutes(10);
        private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(3);

        // [0,2)와 [7,10)은 가동 5분, 마지막 성공 2분부터 [2,7)은 공백 5분이다.
        [Fact]
        public void CommunicationGapStartsAtLastSuccessfulSample()
        {
            var result = Calculate(new[] { Sample(0), Sample(2), Sample(7) },
                Fault(1, FaultStatus.Closed, 2));

            Assert.Equal(Minutes(5), result.UptimeDuration);
            Assert.Equal(Minutes(5), result.GapDuration);
            Assert.Equal(Minutes(5), result.ObservedDuration);
            Assert.Equal(Minutes(5), result.Mtbf.Value);
            Assert.Equal(Minutes(1), result.Mttr.Value);
        }

        // 가동 10분, 고장 0건이면 MTBF와 MTTR은 산출 불가, 가용도는 (10-0)/10=1이다.
        [Fact]
        public void NoFaultsMakesMtbfUnavailableAndAvailabilityOne()
        {
            var result = Calculate(RunningSamples());

            Assert.Equal(0, result.FaultCount);
            Assert.Equal(0, result.UnclosedFaultCount);
            Assert.Equal(MetricUnavailableReason.NoFaults, result.Mtbf.Reason);
            Assert.False(result.Mtbf.HasValue);
            Assert.Throws<InvalidOperationException>(() => { var unused = result.Mtbf.Value; });
            Assert.Equal(MetricUnavailableReason.NoClosedFaults, result.Mttr.Reason);
            Assert.Equal(1.0, result.Availability.Value);
            Assert.Null(result.Availability.Reason);
            Assert.Null(result.Availability.Detail);
        }

        // 첫 샘플이 1분이면 T보다 짧아도 [0,1)은 공백, [1,10)은 가동 9분이다.
        [Fact]
        public void StartBoundaryBeforeFirstSampleIsAlwaysGap()
        {
            var result = Calculate(new[] { Sample(1), Sample(4), Sample(7) });

            Assert.Equal(Minutes(1), result.GapDuration);
            Assert.Equal(Minutes(9), result.UptimeDuration);
        }

        // 마지막 7분이면 Δ=T라 끝까지 가동하고, 6분이면 Δ>T라 [6,10) 전체가 공백이다.
        [Theory]
        [InlineData(7, 10, 0)]
        [InlineData(6, 6, 4)]
        public void EndBoundaryUsesLastStatusOnlyWithinThreshold(int last, int uptime, int gap)
        {
            var result = Calculate(new[] { Sample(0), Sample(2), Sample(4), Sample(last - 1), Sample(last) });

            Assert.Equal(Minutes(uptime), result.UptimeDuration);
            Assert.Equal(Minutes(gap), result.GapDuration);
        }

        // 샘플 0건이면 10분 전체가 공백이라 관측·가동·관측된 정지가 모두 0분이다.
        [Fact]
        public void NoSamplesMakesAvailabilityUnavailableEvenWithAFault()
        {
            var result = Calculate(new ReliabilitySample[0], Fault(0, FaultStatus.Registered));

            Assert.Equal(Minutes(10), result.GapDuration);
            Assert.Equal(TimeSpan.Zero, result.ObservedDuration);
            Assert.Equal(TimeSpan.Zero, result.UptimeDuration);
            Assert.Equal(TimeSpan.Zero, result.FaultDownDuration);
            Assert.Equal(TimeSpan.Zero, result.Mtbf.Value);
            Assert.Equal(MetricUnavailableReason.ZeroObservedTime, result.Availability.Reason);
        }

        // [0,3)은 Δ=T라 가동 3분, [3,7)은 Δ>T라 공백 4분, [7,10)은 가동 3분이다.
        [Fact]
        public void GapThresholdComparisonIncludesEquality()
        {
            var result = Calculate(new[] { Sample(0), Sample(3), Sample(7) });

            Assert.Equal(Minutes(6), result.UptimeDuration);
            Assert.Equal(Minutes(4), result.GapDuration);
        }

        // 1분부터 유효 상태가 돌아오는 3분까지 2분이 공백이고, 나머지 8분은 가동이다.
        [Theory]
        [InlineData(null)]
        [InlineData(3)]
        public void InvalidStatusMakesItsIntervalAGap(int? status)
        {
            var result = Calculate(new[] { Sample(0), Sample(1, (byte?)status), Sample(3), Sample(5), Sample(7) });

            Assert.Equal(Minutes(8), result.UptimeDuration);
            Assert.Equal(Minutes(2), result.GapDuration);
            Assert.Equal(Minutes(8), result.ObservedDuration);
        }

        // 마지막 무효 상태가 시작되는 7분부터 끝까지 3분이 공백이다.
        [Fact]
        public void InvalidLastStatusMakesTailAGap()
        {
            var result = Calculate(new[] { Sample(0), Sample(3), Sample(6), Sample(7, null) });

            Assert.Equal(Minutes(7), result.UptimeDuration);
            Assert.Equal(Minutes(3), result.GapDuration);
        }

        // [0,3) 정지와 [3,6) 이상도 관측되므로 관측 10분, 공백 0분, 가동 4분이다.
        [Fact]
        public void StoppedAndAbnormalStatusesAreObservedButNotUptime()
        {
            var result = Calculate(new[] { Sample(0, 0), Sample(3, 2), Sample(6), Sample(7) });

            Assert.Equal(Minutes(4), result.UptimeDuration);
            Assert.Equal(TimeSpan.Zero, result.GapDuration);
            Assert.Equal(Minutes(10), result.ObservedDuration);
        }

        // 측정값 NaN은 상태 전용 입력에 들어오지 않으므로 코드 1인 10분 전체가 가동이다.
        [Fact]
        public void MeasurementNaNIsAbsentFromInputAndValidRunningStatusCounts()
        {
            Assert.Equal(Minutes(10), Calculate(RunningSamples()).UptimeDuration);
        }

        // 3분 다음에 2분이 오면 구간 길이가 -1분이므로 입력을 거부한다.
        [Fact]
        public void SamplesInDescendingOrderAreRejected()
        {
            Assert.Throws<ArgumentException>(() => Calculate(new[] { Sample(3), Sample(2) }));
        }

        // 동일한 0분의 마지막 상태 1을 이어서 적용하면 10분 모두 가동이다.
        [Fact]
        public void EqualTimestampsUseLastStatusAndStreamIsEnumeratedOnce()
        {
            int enumerations = 0;
            IEnumerable<ReliabilitySample> Stream()
            {
                Assert.Equal(1, ++enumerations);
                yield return Sample(0, 2);
                foreach (var sample in RunningSamples())
                    yield return sample;
            }

            Assert.Equal(Minutes(10), Calculate(Stream()).UptimeDuration);
            Assert.Equal(1, enumerations);
        }

        // 가동 10분/고장 3건, 종결 1건의 복구 시간은 2분, 나머지 2건은 미종결이다.
        [Fact]
        public void OpenFaultsCountForMtbfButNotMttr()
        {
            var result = Calculate(RunningSamples(), Fault(1, FaultStatus.Closed, 3),
                Fault(4, FaultStatus.Registered), Fault(8, FaultStatus.InProgress));

            Assert.Equal(3, result.FaultCount);
            Assert.Equal(1, result.ClosedFaultCount);
            Assert.Equal(2, result.UnclosedFaultCount);
            Assert.Equal(TimeSpan.FromTicks(Minutes(10).Ticks / 3), result.Mtbf.Value);
            Assert.Equal(Minutes(2), result.Mttr.Value);
        }

        // 취소하면 고장·정지 0, 복원하면 고장 1건과 정지 [2,10) 8분이 다시 잡힌다.
        [Fact]
        public void CancelledFaultIsExcludedAndRestoredFaultIsIncludedAgain()
        {
            var cancelled = Calculate(RunningSamples(), Fault(2, FaultStatus.Cancelled, 5));
            var restored = Calculate(RunningSamples(), Fault(2, FaultStatus.Registered));

            Assert.Equal(0, cancelled.FaultCount);
            Assert.Equal(0, cancelled.ClosedFaultCount);
            Assert.Equal(0, cancelled.UnclosedFaultCount);
            Assert.Equal(MetricUnavailableReason.NoFaults, cancelled.Mtbf.Reason);
            Assert.Equal(MetricUnavailableReason.NoClosedFaults, cancelled.Mttr.Reason);
            Assert.Equal(1.0, cancelled.Availability.Value);
            Assert.Equal(1, restored.FaultCount);
            Assert.Equal(1, restored.UnclosedFaultCount);
            Assert.Equal(Minutes(10), restored.Mtbf.Value);
            Assert.Equal(Minutes(8), restored.FaultDownDuration);
            Assert.Equal(0.2, restored.Availability.Value, 12);
        }

        // 알람 8분, 완료 13분이면 MTTR은 5분이고 기간 내 정지는 [8,10) 2분이다.
        [Fact]
        public void CompletionAfterQueryEndStillCountsForMttr()
        {
            var result = Calculate(RunningSamples(), Fault(8, FaultStatus.Closed, 13));

            Assert.Equal(1, result.ClosedFaultCount);
            Assert.Equal(Minutes(5), result.Mttr.Value);
            Assert.Equal(Minutes(2), result.FaultDownDuration);
            Assert.Equal(0.8, result.Availability.Value, 12);
        }

        // 2분에 시작한 고장은 처음 4분에 닫혀 MTTR 2분, 재개 중 산출 불가, 9분에 다시 닫혀 7분이다.
        [Fact]
        public void ReclosedFaultUsesLatestCompletionTime()
        {
            var first = Calculate(RunningSamples(), Fault(2, FaultStatus.Closed, 4));
            var reopened = Calculate(RunningSamples(), Fault(2, FaultStatus.InProgress, 4));
            var reclosed = Calculate(RunningSamples(), Fault(2, FaultStatus.Closed, 9));

            Assert.Equal(Minutes(2), first.Mttr.Value);
            Assert.Equal(MetricUnavailableReason.NoClosedFaults, reopened.Mttr.Reason);
            Assert.Equal(Minutes(8), reopened.FaultDownDuration);
            Assert.Equal(Minutes(7), reclosed.Mttr.Value);
        }

        // 미종결 1건만 있으면 종결 0건, [6,10) 정지 4분, 가용도 (10-4)/10=0.6이다.
        [Fact]
        public void NoClosedFaultsReportsOpenCountAndDowntimeToQueryEnd()
        {
            var result = Calculate(RunningSamples(), Fault(6, FaultStatus.InProgress));

            Assert.Equal(0, result.ClosedFaultCount);
            Assert.Equal(1, result.UnclosedFaultCount);
            Assert.Equal(MetricUnavailableReason.NoClosedFaults, result.Mttr.Reason);
            Assert.Equal(Minutes(4), result.FaultDownDuration);
            Assert.Equal(0.6, result.Availability.Value, 12);
        }

        // [-2,3)은 [0,3)으로 자르고, 10분 시작 고장은 제외한다. 가용도 (10-3)/10=0.7이다.
        [Fact]
        public void CarryInDowntimeIsClippedAndExclusiveEndIsRespected()
        {
            var result = Calculate(RunningSamples(), Fault(-2, FaultStatus.Closed, 3),
                Fault(-4, FaultStatus.Closed, 0), Fault(10, FaultStatus.Registered));

            Assert.Equal(0, result.FaultCount);
            Assert.Equal(0, result.ClosedFaultCount);
            Assert.Equal(0, result.UnclosedFaultCount);
            Assert.Equal(Minutes(3), result.FaultDownDuration);
            Assert.Equal(0.7, result.Availability.Value, 12);
        }

        // [1,6) ∪ [4,8)=[1,8) 7분, MTTR=(5+4)/2=4.5분, 가용도 (10-7)/10=0.3이다.
        [Fact]
        public void OverlappingDowntimeIsChargedOnce()
        {
            var result = Calculate(RunningSamples(), Fault(4, FaultStatus.Closed, 8),
                Fault(1, FaultStatus.Closed, 6));

            Assert.Equal(Minutes(7), result.FaultDownDuration);
            Assert.Equal(TimeSpan.FromSeconds(270), result.Mttr.Value);
            Assert.Equal(0.3, result.Availability.Value, 12);
        }

        // 최대 시각의 틱을 M이라 하면 기간은 1틱, MTTR은 (M+M+M-1)/3의 몫인 M-1틱이다.
        [Fact]
        public void AverageRepairTimeTruncatesTicksWithoutOverflowingTheSum()
        {
            var from = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
            var last = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
            var step = TimeSpan.FromTicks(1);
            var result = ReliabilityCalculator.Calculate(from, from + step, step,
                new[] { new ReliabilitySample(from, 1) },
                new[] { new ReliabilityFault(from, FaultStatus.Closed, last),
                    new ReliabilityFault(from, FaultStatus.Closed, last),
                    new ReliabilityFault(from, FaultStatus.Closed, last - step) });

            Assert.Equal(TimeSpan.FromTicks(last.Ticks - 1), result.Mttr.Value);
            Assert.Equal(step, result.FaultDownDuration);
            Assert.Equal(0.0, result.Availability.Value);
        }

        // 공백 [2,7) 5분이 분모에서 빠진다. 정지 [1,9) 중 관측된 [1,2)+[7,9)=3분만 차감해 (5-3)/5=0.4이다.
        [Fact]
        public void DowntimeDuringGapIsExcludedFromBothSidesOfAvailability()
        {
            var result = Calculate(new[] { Sample(0), Sample(2), Sample(7) },
                Fault(1, FaultStatus.Closed, 9));

            Assert.Equal(Minutes(5), result.ObservedDuration);
            Assert.Equal(Minutes(3), result.FaultDownDuration);
            Assert.Equal(0.4, result.Availability.Value, 12);
        }

        // [1,3), [2,4), [4,5)는 [1,5)로 병합되고, 빈 [6,6)은 사라진다.
        [Fact]
        public void MergeCombinesUnsortedOverlappingAndAdjacentIntervals()
        {
            var merged = ReliabilityIntervals.Merge(new[] { Interval(4, 5), Interval(6, 6),
                Interval(2, 4), Interval(1, 3), Interval(8, 9) });

            Assert.Equal(new[] { Interval(1, 5), Interval(8, 9) }, merged.ToArray());
        }

        // 정지 [0,4), [5,9)에서 공백 [2,7)을 빼면 [0,2), [7,9)만 남는다. 빈 공백 [1,1)은 영향이 없다.
        [Fact]
        public void IntersectObservedReusesGapAcrossMultipleFaultIntervals()
        {
            var observed = ReliabilityIntervals.IntersectObserved(new[] { Interval(0, 4), Interval(5, 9) },
                new[] { Interval(1, 1), Interval(2, 7) });

            Assert.Equal(new[] { Interval(0, 2), Interval(7, 9) }, observed.ToArray());
        }

        // 시각 값이 같아도 Utc 표기가 없는 기간 양 끝과 샘플·고장 시각은 모두 거부한다.
        [Fact]
        public void NonUtcTimestampsAreRejected()
        {
            var local = DateTime.SpecifyKind(From, DateTimeKind.Local);
            var unspecified = DateTime.SpecifyKind(To, DateTimeKind.Unspecified);

            Assert.Throws<ArgumentException>(() => Run(local, To, Threshold));
            Assert.Throws<ArgumentException>(() => Run(From, unspecified, Threshold));
            Assert.Throws<ArgumentException>(() => new ReliabilitySample(local, 1));
            Assert.Throws<ArgumentException>(() => new ReliabilityFault(local, FaultStatus.Registered, null));
            Assert.Throws<ArgumentException>(() => new ReliabilityFault(From, FaultStatus.Closed, unspecified));
        }

        // 길이 0 또는 -10분인 기간과 0 또는 음수인 공백 임계값은 거부한다.
        [Fact]
        public void NonPositivePeriodAndGapThresholdAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Run(From, From, Threshold));
            Assert.Throws<ArgumentOutOfRangeException>(() => Run(To, From, Threshold));
            Assert.Throws<ArgumentOutOfRangeException>(() => Run(From, To, TimeSpan.Zero));
            Assert.Throws<ArgumentOutOfRangeException>(() => Run(From, To, Minutes(-1)));
        }

        // [0,10) 밖의 -1분과 10분 샘플은 구간 계산에 사용할 수 없다.
        [Fact]
        public void SamplesOutsideHalfOpenPeriodAreRejected()
        {
            Assert.Throws<ArgumentException>(() => Calculate(new[] { Sample(-1) }));
            Assert.Throws<ArgumentException>(() => Calculate(new[] { Sample(10) }));
        }

        // 발생 3분에 완료가 없거나 2분에 완료되면 복구 시간을 계산할 수 없다.
        [Theory]
        [InlineData(null)]
        [InlineData(2)]
        public void ClosedFaultRequiresCompletionAtOrAfterAlarm(int? completion)
        {
            Assert.Throws<ArgumentException>(() => Calculate(RunningSamples(),
                Fault(3, FaultStatus.Closed, completion)));
        }

        private static EquipmentReliability Calculate(IEnumerable<ReliabilitySample> samples,
            params ReliabilityFault[] faults)
        {
            return ReliabilityCalculator.Calculate(From, To, Threshold, samples, faults);
        }

        private static EquipmentReliability Run(DateTime from, DateTime to, TimeSpan threshold)
        {
            return ReliabilityCalculator.Calculate(from, to, threshold, RunningSamples(), new ReliabilityFault[0]);
        }

        private static ReliabilitySample[] RunningSamples()
        {
            return new[] { Sample(0), Sample(3), Sample(6), Sample(7) };
        }

        private static TimeSpan Minutes(int minutes) => TimeSpan.FromMinutes(minutes);

        private static ReliabilitySample Sample(int minute, byte? status = 1)
        {
            return new ReliabilitySample(From.AddMinutes(minute), status);
        }

        private static ReliabilityFault Fault(int start, FaultStatus status, int? end = null)
        {
            return new ReliabilityFault(From.AddMinutes(start), status, end.HasValue
                ? From.AddMinutes(end.Value)
                : (DateTime?)null);
        }

        private static ReliabilityInterval Interval(int start, int end) => new ReliabilityInterval(start, end);
    }
}
