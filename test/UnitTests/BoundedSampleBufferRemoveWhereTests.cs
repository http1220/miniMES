using System;
using Core;
using Xunit;

namespace UnitTests
{
    public class BoundedSampleBufferRemoveWhereTests
    {
        [Fact]
        public void RemoveWhere_SomeItemsMatch_PreservesOrderAndReturnsCount()
        {
            var buffer = new BoundedSampleBuffer<int>(4);
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);
            buffer.Add(4);

            Assert.Equal(2, buffer.RemoveWhere(item => item == 2 || item == 3));
            Assert.Equal(new[] { 1, 4 }, buffer.GetSnapshot());
        }

        [Fact]
        public void RemoveWhere_NoItemsMatch_ReturnsZero()
        {
            var buffer = Create(1, 2, 3);

            Assert.Equal(0, buffer.RemoveWhere(item => item == 4));
            Assert.Equal(new[] { 1, 2, 3 }, buffer.GetSnapshot());
        }

        [Fact]
        public void RemoveWhere_WithPriorOverflow_PreservesCounter()
        {
            var buffer = Create(1, 2, 3);
            buffer.Add(4);
            Assert.Equal(1L, buffer.DroppedCount);

            Assert.Equal(1, buffer.RemoveWhere(item => item == 3));
            Assert.Equal(1L, buffer.DroppedCount);
        }

        [Fact]
        public void RemoveWhere_FromFullBuffer_NextAddUsesAvailableSpace()
        {
            var buffer = Create(1, 2, 3);
            buffer.RemoveWhere(item => item == 2);

            buffer.Add(4);

            Assert.Equal(new[] { 1, 3, 4 }, buffer.GetSnapshot());
            Assert.Equal(0L, buffer.DroppedCount);
        }

        [Fact]
        public void RemoveWhere_NullPredicate_Throws()
        {
            var buffer = Create(1);

            Assert.Throws<ArgumentNullException>("match", () => buffer.RemoveWhere(null));
        }

        [Fact]
        public void RemoveWhere_DuplicatesMatch_RemovesBoth()
        {
            var buffer = Create(2, 1, 2);

            Assert.Equal(2, buffer.RemoveWhere(item => item == 2));
            Assert.Equal(new[] { 1 }, buffer.GetSnapshot());
        }

        [Fact]
        public void RemoveWhere_FromFullBuffer_TwoAddsDropOldestSurvivor()
        {
            var buffer = Create(1, 2, 3);
            buffer.RemoveWhere(item => item == 2);

            buffer.Add(4);
            buffer.Add(5);

            Assert.Equal(new[] { 3, 4, 5 }, buffer.GetSnapshot());
            Assert.Equal(1L, buffer.DroppedCount);
        }

        private static BoundedSampleBuffer<int> Create(params int[] items)
        {
            var buffer = new BoundedSampleBuffer<int>(3);
            foreach (int item in items)
            {
                buffer.Add(item);
            }

            return buffer;
        }
    }
}
