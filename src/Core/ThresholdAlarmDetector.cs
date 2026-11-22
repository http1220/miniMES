using System;

namespace Core
{
    public class ThresholdAlarmDetector
    {
        private int consecutiveExceededCount;
        private int consecutiveNormalCount;
        private bool isAlarmActive;

        private readonly int consecutiveCount;

        public ThresholdAlarmDetector() : this(3)
        {
        }

        public ThresholdAlarmDetector(int consecutiveCount)
        {
            if (consecutiveCount < 1)
                throw new ArgumentOutOfRangeException(nameof(consecutiveCount));
            this.consecutiveCount = consecutiveCount;
        }

        public AlarmEvent Update(float value, float upperLimit)
        {
            if(float.IsNaN(value) || float.IsPositiveInfinity(value) || float.IsNegativeInfinity(value))
            {
                return AlarmEvent.None;
            }


            if (value > upperLimit)
            {
                consecutiveExceededCount = consecutiveExceededCount < consecutiveCount ? consecutiveExceededCount + 1 : consecutiveCount;
                consecutiveNormalCount = 0;

                if (consecutiveExceededCount >= consecutiveCount && !isAlarmActive)
                {
                    isAlarmActive = true;
                    return AlarmEvent.Raised;
                }
            }
            else
            {
                consecutiveNormalCount = consecutiveNormalCount < consecutiveCount ? consecutiveNormalCount + 1 : consecutiveCount;
                consecutiveExceededCount = 0;

                if (consecutiveNormalCount >= consecutiveCount && isAlarmActive)
                {
                    isAlarmActive = false;
                    return AlarmEvent.Cleared;
                }
            }

            return AlarmEvent.None;
        }
    }
}