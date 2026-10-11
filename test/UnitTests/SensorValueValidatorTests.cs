using Xunit;
using Core;

namespace UnitTests
{
    public class SensorValueValidatorTests
    {

        // [Fact]
        // public void Test1()
        // {
        //     float value = 101f;
        //     float min = 0f;
        //     float max = 100f;

        //     bool result = SensorValueValidator.IsRange(value, min, max);

        //     Assert.False(result);
        // }

        // [Fact]
        // public void Test2()
        // {
        //     float valueWithinRange = 25f;
        //     float minimumAllowedValue = 0f;
        //     float maximumAllowedValue = 100f;

        //     bool isValid = SensorValueValidator.IsRange(valueWithinRange, minimumAllowedValue, maximumAllowedValue);
        //     Assert.True(isValid);
        // }

        // [Fact]
        // public void Test3()
        // {
        //     float valueAboveMaximum = 101f;
        //     float minimumAllowedValue = 0f;
        //     float maximumAllowedValue = 100f;

        //     bool isValid = SensorValueValidator.IsRange(valueAboveMaximum, minimumAllowedValue, maximumAllowedValue);
        //     Assert.False(isValid);
        // }

        // [Fact]
        // public void Test4()
        // {
        //     float valueAtMinimum = 0f;
        //     float minimumAllowedValue = 0f;
        //     float maximumAllowedValue = 100f;

        //     bool isValid = SensorValueValidator.IsRange(valueAtMinimum, minimumAllowedValue, maximumAllowedValue);
        //     Assert.True(isValid);
        // }

        // [Fact]
        // public void Test5()
        // {
        //     float valueAtMaximum = 100f;
        //     float minimumAllowedValue = 0f;
        //     float maximumAllowedValue = 100f;

        //     bool isValid = SensorValueValidator.IsRange(valueAtMaximum, minimumAllowedValue, maximumAllowedValue);
        //     Assert.True(isValid);
        // }

        // [Fact]
        // public void IsRange_ValueBelowMinimum_ReturnsFalse()
        // {
        //     float valueBelowMinimum = -1f;
        //     float minimumAllowedValue = 0f;
        //     float maximumAllowedValue = 100f;

        //     bool isValid = SensorValueValidator.IsRange(valueBelowMinimum, minimumAllowedValue, maximumAllowedValue);
        //     Assert.False(isValid);
        // }

        // [Fact]
        // public void IsRange_ValueWithinRange_ReturnsTrue()
        // {
        //     float valueWithinRange = 25.5f;
        //     float minimumAllowedValue = 0f;
        //     float maximumAllowedValue = 100f;

        //     bool isValid = SensorValueValidator.IsRange(valueWithinRange, minimumAllowedValue, maximumAllowedValue);
        //     Assert.True(isValid);
        // }

        [Fact]
        public void IsRange_NaN_ReturnsFalse()
        {
            float valueNaN = float.NaN;
            float minimumAllowedValue = 0f;
            float maximumAllowedValue = 100f;

            bool isValid = SensorValueValidator.IsRange(valueNaN, minimumAllowedValue, maximumAllowedValue);
            Assert.False(isValid);
        }

        [Fact]
        public void IsRange_PositiveInfinity_ReturnsFalse()
        {
            float valuePositiveInfinity = float.PositiveInfinity;
            float minimumAllowedValue = 0f;
            float maximumAllowedValue = 100f;

            bool isValid = SensorValueValidator.IsRange(valuePositiveInfinity, minimumAllowedValue, maximumAllowedValue);
            Assert.False(isValid);
        }

        [Fact]
        public void IsRange_NegativeInfinity_ReturnsFalse()
        {
            float valueNegativeInfinity = float.NegativeInfinity;
            float minimumAllowedValue = 0f;
            float maximumAllowedValue = 100f;

            bool isValid = SensorValueValidator.IsRange(valueNegativeInfinity, minimumAllowedValue, maximumAllowedValue);
            Assert.False(isValid);
        }

        [Fact]
        public void IsRange_SameValueWithDifferentRanges_ReturnsDifferentResults()
        {
            float value = 75f;
            float maximumAllowedValueForA = 100f;
            float minimumAllowedValueForA = 0f;

            float maximumAllowedValueForB = 50f;
            float minimumAllowedValueForB = 0f;
            
            bool isValidForA = SensorValueValidator.IsRange(value, minimumAllowedValueForA, maximumAllowedValueForA);
            Assert.True(isValidForA);

            bool isValidForB = SensorValueValidator.IsRange(value, minimumAllowedValueForB, maximumAllowedValueForB);
            Assert.False(isValidForB);
        }


        // [Fact]
        // public void IsValidStatusCode_Code2_ReturnsTrue()
        // {
        //     Assert.True(SensorValueValidator.IsValidStatusCode(2));
        // }

        // [Fact]
        // public void IsValidStatusCode_Code3_ReturnsFalse()
        // {
        //     Assert.False(SensorValueValidator.IsValidStatusCode(3));
        // }

        // [Fact]
        // public void IsValidStatusCode_Code0_ReturnsTrue()
        // {
        //     Assert.True(SensorValueValidator.IsValidStatusCode(0));
        // }


        // [Fact]
        // public void IsValidStatusCode_Code1_ReturnsTrue()
        // {
        //     Assert.True(SensorValueValidator.IsValidStatusCode(1));
        // }

        // [Fact]
        // public void IsValidstatusCode_Code65535_ReturnsFalse()
        // {
        //     Assert.False(SensorValueValidator.IsValidStatusCode(65535));
        // }


        [Theory]
        [InlineData(0, true)]
        [InlineData(1, true)]
        [InlineData(2, true)]
        [InlineData(3, false)]
        [InlineData(65535, false)]
        public void IsValidStatusCode_ReturnsExpectedResult(ushort statusCode , bool expected)
        {
           bool actual = SensorValueValidator.IsValidStatusCode(statusCode);
           Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(25.5f, 0f, 100f, true)]
        [InlineData(-1f, 0f, 100f, false)]
        [InlineData(101f, 0f, 100f, false)]
        [InlineData(0f, 0f, 100f, true)]
        [InlineData(100f, 0f, 100f, true)]
        public void IsRange_ReturnsExpectedResult(float value, float min, float max, bool expected)
        {
            bool actual = SensorValueValidator.IsRange(value, min, max);
            Assert.Equal(expected, actual);
        }


        [Fact]
        public void ValidateSample_ValidInputs_PreservesBothValues()
        {
            float value = 25.5f;
            float min = 0f;
            float max = 100f;
            ushort statusCode = 1;

            SampleValidationResult result = SensorValueValidator.ValidateSample(value, min, max, statusCode);

            Assert.Equal(value, result.Measurement);
            Assert.Equal(statusCode, result.StatusCode);
            Assert.False(result.IsInvalid);
        }


        [Fact]
        public void ValidateSample_NaNMeasurement_PreservesValidStatus()
        {
            float value = float.NaN;
            float min = 0f;
            float max = 100f;
            ushort statusCode = 1;

            SampleValidationResult result = SensorValueValidator.ValidateSample(value, min, max, statusCode);

            Assert.Null(result.Measurement);
            Assert.Equal(statusCode, result.StatusCode);
            Assert.True(result.IsInvalid);
        }

        [Fact]
        public void ValidateSample_InvalidStatus_PreservesValidMeasurement()
        {
            float value = 25.5f;
            float min = 0f;
            float max = 100f;
            ushort statusCode = 3;

            SampleValidationResult result = SensorValueValidator.ValidateSample(value, min, max, statusCode);

            Assert.Equal(value, result.Measurement);
            Assert.Null(result.StatusCode);
            Assert.True(result.IsInvalid);
        }

        [Fact]
        public void ValidateSample_InvalidMeasurementAndStatus_ClearsBothValues()
        {
            float value = 101f;
            float min = 0f;
            float max = 100f;
            ushort statusCode = 3;

            SampleValidationResult result = SensorValueValidator.ValidateSample(value, min, max, statusCode);

            Assert.Null(result.Measurement);
            Assert.Null(result.StatusCode);
            Assert.True(result.IsInvalid);
        }

        [Theory]
        [InlineData(0, 0f, 100f, 1)]
        [InlineData(100, 0f, 100f, 1)]
        public void ValidateSample_BoundaryMeasurement_PreservesBothValues(float value, float min, float max, ushort statusCode)
        {
            SampleValidationResult result = SensorValueValidator.ValidateSample(value, min, max, statusCode);

            Assert.Equal(value, result.Measurement);
            Assert.Equal(statusCode, result.StatusCode);
            Assert.False(result.IsInvalid);
        }
    }
}