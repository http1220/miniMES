using Xunit;
using Core;

namespace UnitTests
{
    public class PendingAlarmTrackerTests
    {
        [Fact]
        public void TryCancelOnClear_UnsentRaiseForSameCause_ReturnsTrue()
        {
            PendingAlarmTracker tracker = new PendingAlarmTracker();

            tracker.MarkRaisedUnsent("A");

            Assert.True(tracker.TryCancelOnClear("A"));
        }

        [Fact]
        public void TryCancelOnClear_WithoutUnsentRaise_ReturnsFalse()
        {
             PendingAlarmTracker tracker = new PendingAlarmTracker();

             Assert.False(tracker.TryCancelOnClear("B"));
        }

        [Fact]
        public void TryCancelOnClear_DifferentCause_PreservesOriginalRecord()
        {
            PendingAlarmTracker tracker = new PendingAlarmTracker();

            tracker.MarkRaisedUnsent("A");

            Assert.False(tracker.TryCancelOnClear("B"));

            Assert.True(tracker.TryCancelOnClear("A"));
        }

        [Fact]
        public void TryCancelOnClear_AfterCancellation_ReturnsFalse()
        {
            PendingAlarmTracker tracker = new PendingAlarmTracker();

            tracker.MarkRaisedUnsent("A");
            Assert.True(tracker.TryCancelOnClear("A"));
            Assert.False(tracker.TryCancelOnClear("A"));
        }
    }
}