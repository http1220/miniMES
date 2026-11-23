using System;
using Core;
using Xunit;

namespace UnitTests
{
    public class ClockReferenceTests
    {
        private static readonly DateTime Noon = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void TryToUtc_WithoutReference_ReturnsFalse()
        {
            var clock = new ClockReference(TimeSpan.FromSeconds(1));

            Assert.False(clock.HasReference);
            Assert.False(clock.TryToUtc(TimeSpan.Zero, out _));
        }

        [Fact]
        public void Update_FirstReference_AllowsForwardAndBackwardProjection()
        {
            var clock = new ClockReference(TimeSpan.FromSeconds(1));

            Assert.Null(clock.Update(TimeSpan.FromSeconds(10), Noon));
            Assert.True(clock.HasReference);
            AssertProjection(clock, 10, Noon);
            AssertProjection(clock, 12, Noon.AddSeconds(2));
            AssertProjection(clock, 7, Noon.AddSeconds(-3));
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(999, false)]
        [InlineData(1000, true)]
        [InlineData(-999, false)]
        [InlineData(-1000, true)]
        public void Update_ReportsInclusiveSignedJumpAndAdoptsNewReference(int deltaMilliseconds, bool jumps)
        {
            var clock = new ClockReference(TimeSpan.FromSeconds(1));
            clock.Update(TimeSpan.FromSeconds(10), Noon);
            var newUtc = Noon.AddSeconds(5).AddMilliseconds(deltaMilliseconds);

            var jump = clock.Update(TimeSpan.FromSeconds(15), newUtc);

            if (jumps)
                Assert.Equal(TimeSpan.FromMilliseconds(deltaMilliseconds), Assert.IsType<ClockJump>(jump).Delta);
            else
                Assert.Null(jump);
            AssertProjection(clock, 16, newUtc.AddSeconds(1));
        }

        [Theory]
        [InlineData(9)]
        [InlineData(10)]
        public void Update_StaleResponse_PreservesReference(int receivedAtSeconds)
        {
            var clock = new ClockReference(TimeSpan.FromSeconds(1));
            clock.Update(TimeSpan.FromSeconds(10), Noon);

            Assert.Null(clock.Update(TimeSpan.FromSeconds(receivedAtSeconds), Noon.AddHours(1)));
            AssertProjection(clock, 11, Noon.AddSeconds(1));
        }

        [Theory]
        [InlineData(DateTimeKind.Unspecified)]
        [InlineData(DateTimeKind.Local)]
        public void Update_NonUtc_Throws(DateTimeKind kind)
        {
            var clock = new ClockReference(TimeSpan.FromSeconds(1));

            Assert.Throws<ArgumentException>(() => clock.Update(TimeSpan.Zero, DateTime.SpecifyKind(Noon, kind)));
            Assert.False(clock.HasReference);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Constructor_NonpositiveThreshold_Throws(int milliseconds)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ClockReference(TimeSpan.FromMilliseconds(milliseconds)));
        }

        [Fact]
        public void Apply_ClampsEarlierTimesAndKeepsIndependentEquipmentFloors()
        {
            var floor = new EquipmentUtcFloor();
            var later = Noon.AddSeconds(1);

            Assert.Equal(Noon, floor.Apply(1, Noon));
            Assert.Equal(Noon, floor.Apply(1, Noon.AddSeconds(-1)));
            Assert.Equal(Noon, floor.Apply(1, Noon));
            Assert.Equal(Noon, floor.Apply(1, Noon.AddSeconds(-2)));
            Assert.Equal(Noon.AddSeconds(-3), floor.Apply(2, Noon.AddSeconds(-3)));
            Assert.Equal(later, floor.Apply(1, later));
            Assert.Equal(later, floor.Apply(1, Noon));
            Assert.Equal(Noon.AddSeconds(-3), floor.Apply(2, Noon.AddSeconds(-4)));
        }

        private static void AssertProjection(ClockReference clock, int seconds, DateTime expected)
        {
            Assert.True(clock.TryToUtc(TimeSpan.FromSeconds(seconds), out var utc));
            Assert.Equal(expected, utc);
            Assert.Equal(DateTimeKind.Utc, utc.Kind);
        }
    }
}
