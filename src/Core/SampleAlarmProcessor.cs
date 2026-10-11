namespace Core
{
    public class SampleAlarmProcessor
    {
        public static AlarmEvent Process(SampleValidationResult validationResult, ThresholdAlarmDetector detector, float upperLimit)
        {
            if (validationResult.Measurement.HasValue == false)
            {
                return AlarmEvent.None;
            }
            else
            {
                return detector.Update(validationResult.Measurement.Value, upperLimit);
            }
        }
    }
}