using System;
using Core;
using Xunit;

namespace UnitTests
{
    public class CommunicationAlarmDetectorCountTests
    {
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void OnValidResponse_ConfiguredRecoveryCount_ClearsAtCount(int count)
        {
            var detector = new CommunicationAlarmDetector(count);
            var timeout = TimeSpan.FromSeconds(5);
            detector.OnValidResponse(TimeSpan.Zero);
            Assert.Equal(AlarmEvent.Raised, detector.CheckTimeout(timeout, timeout));
            for (var i = 1; i <= count; i++)
                Assert.Equal(i == count ? AlarmEvent.Cleared : AlarmEvent.None,
                    detector.OnValidResponse(timeout + TimeSpan.FromSeconds(i)));
        }

        [Fact]
        public void CheckTimeout_StartTimeWithoutResponse_RaisesAtBoundaryAndClearsAfterRecovery()
        {
            var start = TimeSpan.FromSeconds(10);
            var timeout = TimeSpan.FromSeconds(5);
            var detector = new CommunicationAlarmDetector(2, start);
            var deadline = start + timeout;

            Assert.Equal(AlarmEvent.None, detector.CheckTimeout(deadline - TimeSpan.FromTicks(1), timeout));
            Assert.Equal(AlarmEvent.Raised, detector.CheckTimeout(deadline, timeout));
            Assert.Equal(AlarmEvent.None, detector.CheckTimeout(deadline + timeout, timeout));
            Assert.Equal(AlarmEvent.None, detector.OnValidResponse(deadline + timeout));
            Assert.Equal(AlarmEvent.Cleared, detector.OnValidResponse(deadline + timeout + TimeSpan.FromTicks(1)));
        }

        [Fact]
        public void CheckTimeout_LegacyConstructorsWithoutResponse_NeverRaise()
        {
            var timeout = TimeSpan.FromSeconds(5);
            foreach (var detector in new[] { new CommunicationAlarmDetector(), new CommunicationAlarmDetector(2) })
            {
                detector.OnFailedResponse();
                Assert.Equal(AlarmEvent.None, detector.CheckTimeout(timeout, timeout));
                Assert.Equal(AlarmEvent.None, detector.CheckTimeout(TimeSpan.MaxValue, timeout));
            }
        }

        [Fact]
        public void CheckTimeout_FirstValidResponse_ReplacesStartTime()
        {
            var start = TimeSpan.FromSeconds(10);
            var timeout = TimeSpan.FromSeconds(5);
            var detector = new CommunicationAlarmDetector(2, start);
            var receivedAt = start + TimeSpan.FromSeconds(3);

            Assert.Equal(AlarmEvent.None, detector.OnValidResponse(receivedAt));
            Assert.Equal(AlarmEvent.None, detector.CheckTimeout(start + timeout, timeout));
            Assert.Equal(AlarmEvent.None, detector.CheckTimeout(receivedAt + timeout - TimeSpan.FromTicks(1), timeout));
            Assert.Equal(AlarmEvent.Raised, detector.CheckTimeout(receivedAt + timeout, timeout));
        }

        [Fact]
        public void Construct_ZeroCount_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CommunicationAlarmDetector(0));
        }
    }
}
