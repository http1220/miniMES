using System;

namespace Core
{
    public class CommunicationAlarmDetector
    {
        private TimeSpan? lastValidResponseTime;
        private bool isAlarmActive; 

        private int consecutiveSuccessCount;

        private readonly int recoverySuccessCount;

        public CommunicationAlarmDetector() : this(3)
        {
        }

        public CommunicationAlarmDetector(int recoverySuccessCount)
        {
            if (recoverySuccessCount < 1)
                throw new ArgumentOutOfRangeException(nameof(recoverySuccessCount));
            this.recoverySuccessCount = recoverySuccessCount;
        }

        public AlarmEvent OnValidResponse(TimeSpan now)
        {
            lastValidResponseTime = now;
            if(consecutiveSuccessCount < recoverySuccessCount)
            {
                consecutiveSuccessCount++;
            }

            if(consecutiveSuccessCount >= recoverySuccessCount && isAlarmActive)
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