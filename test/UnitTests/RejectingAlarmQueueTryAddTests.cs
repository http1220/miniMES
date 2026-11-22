using Core;
using Xunit;

namespace UnitTests
{
    public class RejectingAlarmQueueTryAddTests
    {
        [Fact]
        public void TryAdd_WithinCapacity_ReturnsTrueWithoutRejection()
        {
            var queue = new RejectingAlarmQueue<int>(1);

            Assert.True(queue.TryAdd(1));
            Assert.Equal(new[] { 1 }, queue.GetSnapshot());
            Assert.Equal(0L, queue.RejectedCount);
        }

        [Fact]
        public void TryAdd_WhenFull_ReturnsFalseAndCountsRejection()
        {
            var queue = new RejectingAlarmQueue<int>(1);
            queue.Add(1);

            Assert.False(queue.TryAdd(2));
            Assert.Equal(new[] { 1 }, queue.GetSnapshot());
            Assert.Equal(1L, queue.RejectedCount);
        }

        [Fact]
        public void TryAdd_SameInputsAsAdd_ProducesSameQueueState()
        {
            var queue = new RejectingAlarmQueue<int>(2);
            var legacyQueue = new RejectingAlarmQueue<int>(2);
            foreach (int item in new[] { 1, 2, 3 })
            {
                queue.TryAdd(item);
                legacyQueue.Add(item);
            }

            Assert.Equal(legacyQueue.GetSnapshot(), queue.GetSnapshot());
            Assert.Equal(legacyQueue.RejectedCount, queue.RejectedCount);
        }
    }
}
