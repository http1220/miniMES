using Xunit;
using Core;
using System;
namespace UnitTests
{
    public class BoundedSampleBufferTests
    {
        [Fact]
        public void GetSnapshot_EmptyBuffer_ReturnsEmpty()
        {
            BoundedSampleBuffer<int> buffer = new BoundedSampleBuffer<int>(3);

            int[] result = buffer.GetSnapshot();

            Assert.Empty(result);
        }

        [Fact]
        public void Add_WithinCapacity_PreservesOrderWithoutDrops()
        {
            BoundedSampleBuffer<int> buffer = new BoundedSampleBuffer<int>(3);
            
            int[] items = new int[]{1,2,3};

            foreach (var item in items)
            {
                buffer.Add(item);
            }
            
            Assert.Equal(items , buffer.GetSnapshot());
            Assert.Equal(0L , buffer.DroppedCount);
        }

        [Fact]
        public void Add_WhenFull_DropsOldestItem()
        {
            BoundedSampleBuffer<int> buffer = new BoundedSampleBuffer<int>(3);
            
            int[] items = new int[]{1,2,3,4};

            foreach (var item in items)
            {
                buffer.Add(item);
            }

            Assert.Equal(1L , buffer.DroppedCount);
        }

        [Fact]
        public void Add_MultipleOverflows_CountsAllDroppedItems()
        {
            BoundedSampleBuffer<int> buffer = new BoundedSampleBuffer<int>(3);
            
            int[] items = new int[]{1,2,3,4,5};

            foreach (var item in items)
            {
                buffer.Add(item);
            }

            int[] expectedItems = new int[]{3,4,5};

            Assert.Equal(expectedItems  , buffer.GetSnapshot());
            Assert.Equal(2L , buffer.DroppedCount);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Constructor_NonPositiveCapacity_Throws(int capacity)
        {
            Assert.Throws<ArgumentOutOfRangeException>(()=> new BoundedSampleBuffer<int>(capacity));
        }

        [Fact]
        public void Add_CapacityOne_KeepsOnlyNewestItem()
        {
            BoundedSampleBuffer<int> buffer = new BoundedSampleBuffer<int>(1);
            
            int[] items = new int[]{1,2,3};

            foreach (var item in items)
            {
                buffer.Add(item);
            }
            int[] expectedItems = new int[]{3};

            Assert.Equal(expectedItems  , buffer.GetSnapshot());
            Assert.Equal(2L , buffer.DroppedCount);
        }
    }

    
}