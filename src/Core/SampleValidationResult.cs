namespace Core
{
    public class SampleValidationResult
    {
        public float? Measurement { get; }
        public ushort? StatusCode { get; }
        public bool IsInvalid { get; }

        public SampleValidationResult(float? measurement, ushort? statusCode)
        {
            Measurement = measurement;
            StatusCode = statusCode;
            IsInvalid = measurement == null || statusCode == null;
        }
    }
}