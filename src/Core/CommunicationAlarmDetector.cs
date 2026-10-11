using System;

namespace Core
{
    public class CommunicationAlarmDetector
    {
        private TimeSpan? lastValidResponseTime;
        private bool isAlarmActive; 

        private int consecutiveSuccessCount;

        public AlarmEvent OnValidResponse(TimeSpan now)
        {
            lastValidResponseTime = now;
            if(consecutiveSuccessCount < 3)
            {
                consecutiveSuccessCount++;
            }

            if(consecutiveSuccessCount >= 3 && isAlarmActive)
            {
                isAlarmActive = false;
                return AlarmEvent.Cleared;
            }


            return AlarmEvent.None;
        }

        public void OnFailedResponse()
        {
            consecutiveSuccessCount = 0;
        }

        public AlarmEvent CheckTimeout(TimeSpan now, TimeSpan timeout)
        {
            if(lastValidResponseTime.HasValue)
            {
                TimeSpan elapsed = now - lastValidResponseTime.Value;
                if(elapsed >= timeout && !isAlarmActive)
                {
                    isAlarmActive = true;
                    consecutiveSuccessCount = 0;
                    return AlarmEvent.Raised;
                }
                else
                {
                    return AlarmEvent.None;
                }
            }
            else
            {
                return AlarmEvent.None;
            }
        }
    }
}