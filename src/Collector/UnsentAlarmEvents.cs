using System.Collections.Generic;
using Core;

namespace Collector
{
    internal sealed class UnsentAlarmEvents
    {
        private readonly SortedDictionary<(int EquipmentId, AlarmCause Cause), LinkedList<AlarmEventItem>> events =
            new SortedDictionary<(int EquipmentId, AlarmCause Cause), LinkedList<AlarmEventItem>>();

        internal int Count { get; private set; }

        internal bool HasEvents((int EquipmentId, AlarmCause Cause) key)
        {
            return events.ContainsKey(key);
        }

        internal bool TryCancelRaised((int EquipmentId, AlarmCause Cause) key)
        {
            if (!events.TryGetValue(key, out var list) || list.Last.Value.EventType != AlarmEventType.Raised)
                return false;

            list.RemoveLast();
            Count--;
            if (list.Count == 0)
                events.Remove(key);
            return true;
        }

        internal void Hold(AlarmEventItem item)
        {
            var key = (item.EquipmentId, item.Cause);
            if (!events.TryGetValue(key, out var list))
            {
                list = new LinkedList<AlarmEventItem>();
                events.Add(key, list);
            }
            list.AddLast(item);
            Count++;
        }

        internal AlarmEventItem[] GetSnapshot()
        {
            var snapshot = new AlarmEventItem[Count];
            var index = 0;
            foreach (var list in events.Values)
            {
                list.CopyTo(snapshot, index);
                index += list.Count;
            }
            return snapshot;
        }

        internal int Requeue(RejectingAlarmQueue<AlarmEventItem> queue, int freeSlots)
        {
            var emptied = new List<(int EquipmentId, AlarmCause Cause)>();
            var moved = 0;
            foreach (var entry in events)
            {
                var list = entry.Value;
                while (list.First != null && freeSlots > 0 && queue.TryAdd(list.First.Value))
                {
                    list.RemoveFirst();
                    moved++;
                    freeSlots--;
                }
                if (list.Count == 0)
                    emptied.Add(entry.Key);
            }

            foreach (var key in emptied)
                events.Remove(key);
            Count -= moved;
            return moved;
        }
    }
}
