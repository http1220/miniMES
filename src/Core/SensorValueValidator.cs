
namespace Core
{
    public class SensorValueValidator
    {
        public static bool IsRange(float value, float min, float max)
        {
            return value >= min && value <= max;
        }

        public static bool IsValidStatusCode(ushort statusCode)
        {
            return statusCode == 0 || statusCode == 1 || statusCode == 2;
        }

        public static SampleValidationResult ValidateSample(float measurement,  float min, float max , ushort statusCode)
        {
            bool isMeasurementValid = IsRange(measurement, min, max);
            bool isStatusCodeValid = IsValidStatusCode(statusCode);


            SampleValidationResult result = new SampleValidationResult(isMeasurementValid ? (float?)measurement : null
            , isStatusCodeValid ? (ushort?)statusCode : null);

            return result;
        }

    }
}