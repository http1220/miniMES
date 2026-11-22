using System;
using Core;
using Xunit;

namespace UnitTests
{
    public class DelayJitterMaximumTests
    {
        [Theory]
        [InlineData(0.5, 0.5, 1250)]
        [InlineData(1, 0, 1000)]
        public void ApplyJitter_ConfiguredMaximum_ScalesDelay(double fraction, double maximum, int milliseconds)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(milliseconds),
                DelayJitter.ApplyJitter(TimeSpan.FromSeconds(1), fraction, maximum));
        }

        [Fact]
        public void ApplyJitter_NegativeMaximum_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => DelayJitter.ApplyJitter(TimeSpan.FromSeconds(1), 1, -0.01));
        }
    }
}
