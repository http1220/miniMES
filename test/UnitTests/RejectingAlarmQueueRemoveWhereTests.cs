using System;
using Core;
using Xunit;

namespace UnitTests
{
    public class RejectingAlarmQueueRemoveWhereTests
    {
        [Fact]
        public void RemoveWhere_SomeItemsMatch_PreservesOrderAndReturnsCount()
        {
            var queue = new RejectingAlarmQueue<int>(4);
            queue.Add(1);
            queue.Add(2);
            queue.Add(3);
            queue.Add(4);

            Assert.Equal(2, queue.RemoveWhere(item => item == 2 || item == 3));
            Assert.Equal(new[] { 1, 4 }, queue.GetSnapshot());
        }

        [Fact]
        public void RemoveWhere_NoItemsMatch_ReturnsZero()
        {
            var queue = Create(1, 2, 3);

            Assert.Equal(0, queue.RemoveWhere(item => item == 4));
            Assert.Equal(new[] { 1, 2, 3 }, queue.GetSnapshot());
        }

        [Fact]
        public void RemoveWhere_WithPriorOverflow_PreservesCounter()
        {
            var queue = Create(1, 2, 3);
            queue.Add(4);
            Assert.Equal(1L, queue.RejectedCount);

            Assert.Equal(1, queue.RemoveWhere(item => item == 3));
            Assert.Equal(1L, queue.RejectedCount);
        }

        [Fact]
        public void RemoveWhere_FromFullQueue_NextAddUsesAvailableSpace()
        {
            var queue = Create(1, 2, 3);
            queue.RemoveWhere(item => item == 2);

            queue.Add(4);

            Assert.Equal(new[] { 1, 3, 4 }, queue.GetSnapshot());
            Assert.Equal(0L, queue.RejectedCount);
        }

        [Fact]
        public void RemoveWhere_NullPredicate_Throws()
        {
            var queue = Create(1);

            Assert.Throws<ArgumentNullException>("match", () => queue.RemoveWhere(null));
        }

        [Fact]
        public void RemoveWhere_DuplicatesMatch_RemovesBoth()
        {
            var queue = Create(2, 1, 2);

            Assert.Equal(2, queue.RemoveWhere(item => item == 2));
            Assert.Equal(new[] { 1 }, queue.GetSnapshot());
        }

        private static RejectingAlarmQueue<int> Create(params int[] items)
        {
            var queue = new RejectingAlarmQueue<int>(3);
            foreach (int item in items)
            {
                queue.Add(item);
            }

            return queue;
        }
    }
}
