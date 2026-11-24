using System;
using Server;
using Xunit;

namespace UnitTests
{
    public sealed class MetricsPeriodTests
    {
        private const string Start = "2026-11-01T00:00:00.000Z";
        private const string End = "2026-11-02T00:00:00.000Z";
        private static readonly DateTime From = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Now = From.AddDays(15).AddTicks(9999);

        [Fact]
        public void TryParse_ReturnsUtcBounds()
        {
            Assert.True(MetricsPeriod.TryParse(Start, End, Now, out var from, out var to));
            Assert.Equal(From, from);
            Assert.Equal(From.AddDays(1), to);
            Assert.Equal(DateTimeKind.Utc, from.Kind);
            Assert.Equal(DateTimeKind.Utc, to.Kind);
        }

        [Theory]
        [InlineData("2026-11-15T00:00:00.000Z", true)]
        [InlineData("2026-11-15T00:00:00.001Z", false)]
        public void TryParse_LimitsPeriodToFourteenDays(string end, bool expected)
        {
            Assert.Equal(expected, MetricsPeriod.TryParse(Start, end, Now, out _, out _));
        }

        [Theory]
        [InlineData("2026-11-16T00:00:00.000Z", true)]
        [InlineData("2026-11-16T00:00:00.001Z", false)]
        public void TryParse_LimitsEndToServerTime(string end, bool expected)
        {
            Assert.Equal(expected, MetricsPeriod.TryParse(End, end, Now, out _, out _));
        }

        [Theory]
        [InlineData(Start)]
        [InlineData("2026-10-31T23:59:59.999Z")]
        public void TryParse_RequiresIncreasingBounds(string end)
        {
            Assert.False(MetricsPeriod.TryParse(Start, end, Now, out _, out _));
        }

        [Theory]
        [InlineData("2026-11-02T09:00:00.000+09:00")]
        [InlineData("2026-11-02T00:00:00.000")]
        [InlineData("2026-11-02T00:00:00Z")]
        [InlineData("2026-11-02T00:00:00.00Z")]
        [InlineData("2026-11-02T00:00:00.000z")]
        [InlineData("")]
        [InlineData(null)]
        public void TryParse_RequiresExactUtcFormatForBothBounds(string invalid)
        {
            Assert.False(MetricsPeriod.TryParse(Start, invalid, Now, out _, out _));
            Assert.False(MetricsPeriod.TryParse(invalid, End, Now, out _, out _));
        }
    }
}
