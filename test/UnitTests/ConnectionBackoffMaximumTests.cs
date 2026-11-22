using System;
using Core;
using Xunit;

namespace UnitTests
{
    public class ConnectionBackoffMaximumTests
    {
        [Theory]
        [InlineData(4, 2, 4)]
        [InlineData(1, 1, 1)]
        public void OnConnectionFailed_ConfiguredMaximum_CapsAndResets(int maximum, int second, int third)
        {
            var backoff = new ConnectionBackoff(TimeSpan.FromSeconds(maximum));
            foreach (var seconds in new[] { 1, second, third, maximum })
                Assert.Equal(TimeSpan.FromSeconds(seconds), backoff.OnConnectionFailed());
            backoff.OnConnectionSucceeded();
            Assert.Equal(TimeSpan.FromSeconds(1), backoff.OnConnectionFailed());
        }

        [Fact]
        public void Construct_BelowOneSecond_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectionBackoff(TimeSpan.FromMilliseconds(999)));
        }
    }
}
