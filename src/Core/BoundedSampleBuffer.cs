using System.Collections.Generic;
using System;

namespace Core
{
    public class BoundedSampleBuffer<T>
    {
        private int capacity;
        private Queue<T> items = new Queue<T>();
        public long DroppedCount {get; private set;}

        public BoundedSampleBuffer(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "용량은 1 이상이어야 합니다.");
            }
            
            this.capacity = capacity;
        }

        public void Add(T item)
        {
            if(items.Count >= capacity)
            {
                items.Dequeue();
                DroppedCount++;
            }

            items.Enqueue(item);
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