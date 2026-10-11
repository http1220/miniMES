namespace Core
{
    public class ThresholdAlarmDetector
    {
        private int consecutiveExceededCount;
        private int consecutiveNormalCount;
        private bool isAlarmActive;

        public AlarmEvent Update(float value, float upperLimit)
        {
            if(float.IsNaN(value) || float.IsPositiveInfinity(value) || float.IsNegativeInfinity(value))
            {
                return AlarmEvent.None;
            }


            if (value > upperLimit)
            {
                consecutiveExceededCount = consecutiveExceededCount < 3 ? consecutiveExceededCount + 1 : 3;
                consecutiveNormalCount = 0;

                if (consecutiveExceededCount >= 3 && !isAlarmActive)
                {
                    isAlarmActive = true;
                    return AlarmEvent.Raised;
                }
            }
            else
            {
                consecutiveNormalCount = consecutiveNormalCount < 3 ? consecutiveNormalCount + 1 : 3;
                consecutiveExceededCount = 0;

                if (consecutiveNormalCount >= 3 && isAlarmActive)
                {
                    isAlarmActive = false;
                    return AlarmEvent.Cleared;
                }
            }

            return AlarmEvent.None;
        }
    }
}