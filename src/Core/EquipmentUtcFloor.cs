using System;
using System.Collections.Generic;

namespace Core
{
    public sealed class EquipmentUtcFloor
    {
        private readonly Dictionary<int, DateTime> floors = new Dictionary<int, DateTime>();

        // The caller supplies items in monotonic order and synchronizes access.
        public DateTime Apply(int equipmentId, DateTime utc)
        {
            if (floors.TryGetValue(equipmentId, out var floor) && utc < floor)
                utc = floor;

            floors[equipmentId] = utc;
            return utc;
        }
    }
}
