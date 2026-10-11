using System.Collections.Generic;
using System;
namespace Core
{
    public class RejectingAlarmQueue<T>
    {
        private int capacity;
        private Queue<T> items = new Queue<T>();
        public long RejectedCount {get; private set;}
        
        public RejectingAlarmQueue(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "용량은 1 이상이어야 합니다.");
            }

            this.capacity = capacity;


        }

        public void Add(T item)
        {
            if(items.Count >= this.capacity)
            {
                RejectedCount++;
                return;
            }

            items.Enqueue(item);

        }

        public T[] GetSnapshot()
        {
            return items.ToArray();
        }
    }
}