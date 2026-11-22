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
            TryAdd(item);
        }

        public bool TryAdd(T item)
        {
            if(items.Count >= this.capacity)
            {
                RejectedCount++;
                return false;
            }

            items.Enqueue(item);
            return true;
        }

        public int RemoveWhere(Predicate<T> match)
        {
            if (match == null)
            {
                throw new ArgumentNullException(nameof(match));
            }

            var remaining = new Queue<T>();
            foreach (T item in items)
            {
                if (!match(item))
                {
                    remaining.Enqueue(item);
                }
            }

            int removed = items.Count - remaining.Count;
            items = remaining;
            return removed;
        }

        public T[] GetSnapshot()
        {
            return items.ToArray();
        }
    }
}