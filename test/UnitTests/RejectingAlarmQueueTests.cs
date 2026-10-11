using Xunit;
using Core;
using System;

namespace UnitTests
{
    public class RejectingAlarmQueueTests
    {
        [Fact]
        public void Add_WithinCapacity_PreservesOrderWithoutRejections()
        {
            RejectingAlarmQueue<int> alarmQueue = new RejectingAlarmQueue<int>(3);

            int[] expectedItems = new int[]{1,2,3};

            foreach (var item in expectedItems)
            {
                alarmQueue.Add(item);   
            }

            int[] result = alarmQueue.GetSnapshot();

            Assert.Equal(expectedItems,result);
            Assert.Equal(0L, alarmQueue.RejectedCount);
        }

        [Fact]
        public void Add_WhenFull_RejectsNewestItem()
        {
            RejectingAlarmQueue<int> alarmQueue = new RejectingAlarmQueue<int>(3);
            int[] expectedItems = new int[]{1,2,3};
            int[] items = new int[]{1,2,3,4};

            foreach (var item in items)
            {
                alarmQueue.Add(item);   
            }

            int[] result = alarmQueue.GetSnapshot();

            Assert.Equal(expectedItems,result);
            Assert.Equal(1L, alarmQueue.RejectedCount);
        }

        [Fact]
        public void Add_MultipleRejections_PreservesExistingItems()
        {
            RejectingAlarmQueue<int> alarmQueue = new RejectingAlarmQueue<int>(3);
            int[] expectedItems = new int[]{1,2,3};
            int[] items = new int[]{1,2,3,4,5};

            foreach (var item in items)
            {
                alarmQueue.Add(item);   
            }

            int[] result = alarmQueue.GetSnapshot();

            Assert.Equal(expectedItems,result);
            Assert.Equal(2L, alarmQueue.RejectedCount);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Constructor_NonPositiveCapacity_Throws(int capacity)
        {
            Assert.Throws<ArgumentOutOfRangeException>(()=> new RejectingAlarmQueue<int>(capacity));
        }
    }
}