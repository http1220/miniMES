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
        public void Construct_ZeroCount_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CommunicationAlarmDetector(0));
        }
    }
}
