using System;
using Core;
using Xunit;

namespace UnitTests
{
    public class ThresholdAlarmDetectorCountTests
    {
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void Update_ConfiguredCount_RaisesAndClearsAtCount(int count)
        {
            var detector = new ThresholdAlarmDetector(count);
            for (var i = 1; i <= count; i++)
                Assert.Equal(i == count ? AlarmEvent.Raised : AlarmEvent.None, detector.Update(2, 1));
            for (var i = 1; i <= count; i++)
                Assert.Equal(i == count ? AlarmEvent.Cleared : AlarmEvent.None, detector.Update(1, 1));
        }

        [Fact]
        public void Construct_ZeroCount_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ThresholdAlarmDetector(0));
        }
    }
}
