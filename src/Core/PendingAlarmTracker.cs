using System.Collections.Generic;

namespace Core
{
    public class PendingAlarmTracker
    {
        private HashSet<string> unsentCauses = new HashSet<string>();

    
        public void MarkRaisedUnsent(string causeKey)
        {
            unsentCauses.Add(causeKey);
        }

        public bool TryCancelOnClear(string causeKey)
        {
            return unsentCauses.Remove(causeKey);
        }
    }
}
