using Xunit;
using Core;
using System.Collections.Generic;

namespace UnitTests
{
    public class ThresholdAlarmDetectorTests
    {
       [Fact]
       public void Update_ThreeNormalValues_ReturnsNoneEachTime()
        {
            ThresholdAlarmDetector detector = new ThresholdAlarmDetector();
            
            AlarmEvent firstEvent = detector.Update(90f, 100f);
            AlarmEvent secondEvent = detector.Update(95f, 100f);
            AlarmEvent thirdEvent = detector.Update(100f, 100f);

            Assert.Equal(AlarmEvent.None, firstEvent);
            Assert.Equal(AlarmEvent.None, secondEvent);
            Assert.Equal(AlarmEvent.None, thirdEvent);
        }

        [Fact]
        public void Update_ThreeConsecutiveExceedances_RaisesOnThird()
        {
            ThresholdAlarmDetector detector = new ThresholdAlarmDetector();
            
            AlarmEvent firstEvent = detector.Update(101f, 100f);
            AlarmEvent secondEvent = detector.Update(102f, 100f);
            AlarmEvent thirdEvent = detector.Update(103f, 100f);

            Assert.Equal(AlarmEvent.None, firstEvent);
            Assert.Equal(AlarmEvent.None, secondEvent);
            Assert.Equal(AlarmEvent.Raised, thirdEvent);
        }


        [Fact]
        public void Update_FiveConsecutiveExceedances_RaisesOnlyOnce()
        {
            ThresholdAlarmDetector detector = new ThresholdAlarmDetector();
            
            AlarmEvent firstEvent = detector.Update(101f, 100f);
            AlarmEvent secondEvent = detector.Update(102f, 100f);
            AlarmEvent thirdEvent = detector.Update(103f, 100f);
            AlarmEvent fourthEvent = detector.Update(104f, 100f);
            AlarmEvent fifthEvent = detector.Update(105f, 100f);

            Assert.Equal(AlarmEvent.None, firstEvent);
            Assert.Equal(AlarmEvent.None, secondEvent);
            Assert.Equal(AlarmEvent.Raised, thirdEvent);
            Assert.Equal(AlarmEvent.None, fourthEvent);
            Assert.Equal(AlarmEvent.None, fifthEvent);
        }

        [Fact]
        public void Update_ThreeNormalValuesWhileActive_ClearsOnThird()
        {
            ThresholdAlarmDetector detector = new ThresholdAlarmDetector();
            
            // First, raise the alarm
            AlarmEvent firstEvent =detector.Update(101f, 100f);
            AlarmEvent secondEvent = detector.Update(102f, 100f);
            AlarmEvent thirdEvent = detector.Update(103f, 100f);

            // Now, provide three normal values
            AlarmEvent fourthEvent  = detector.Update(90f, 100f);
            AlarmEvent fifthEvent = detector.Update(95f, 100f);
            AlarmEvent sixthEvent  = detector.Update(100f, 100f);

            Assert.Equal(AlarmEvent.None, firstEvent);
            Assert.Equal(AlarmEvent.None, secondEvent);
            Assert.Equal(AlarmEvent.Raised, thirdEvent);
            Assert.Equal(AlarmEvent.None, fourthEvent);
            Assert.Equal(AlarmEvent.None, fifthEvent);
            Assert.Equal(AlarmEvent.Cleared, sixthEvent);
        }

        [Fact]
        public void Update_ExceedanceDuringRecovery_RestartsNormalCount()
        {
            ThresholdAlarmDetector detector = new ThresholdAlarmDetector();

            detector.Update(101f, 100f); // Exceed
            detector.Update(102f, 100f); // Exceed
            detector.Update(103f, 100f); // Exceed - Alarm Raised

            AlarmEvent firstEvent = detector.Update(90f, 100f);
            AlarmEvent secondEvent = detector.Update(95f, 100f);
            AlarmEvent thirdEvent = detector.Update(101f, 100f); // Exceed
            AlarmEvent fourthEvent = detector.Update(90f, 100f);
            AlarmEvent fifthEvent = detector.Update(95f, 100f);
            AlarmEvent sixthEvent = detector.Update(100f, 100f);

            Assert.Equal(AlarmEvent.None, firstEvent);
            Assert.Equal(AlarmEvent.None, secondEvent);
            Assert.Equal(AlarmEvent.None, thirdEvent);
            Assert.Equal(AlarmEvent.None, fourthEvent);
            Assert.Equal(AlarmEvent.None, fifthEvent);
            Assert.Equal(AlarmEvent.Cleared, sixthEvent);
        }

        [Fact]
        public void Update_NormalDuringExceedances_RestartsExceededCount()
        {
            ThresholdAlarmDetector detector = new ThresholdAlarmDetector();

            AlarmEvent firstEvent = detector.Update(101f, 100f); 
            AlarmEvent secondEvent = detector.Update(102f, 100f); 
            AlarmEvent thirdEvent = detector.Update(100f, 100f);
            AlarmEvent fourthEvent = detector.Update(101f, 100f);
            AlarmEvent fifthEvent = detector.Update(102f, 100f);
            AlarmEvent sixthEvent = detector.Update(103f, 100f);
            

            Assert.Equal(AlarmEvent.None, firstEvent);
            Assert.Equal(AlarmEvent.None, secondEvent);
            Assert.Equal(AlarmEvent.None, thirdEvent);
            Assert.Equal(AlarmEvent.None, fourthEvent);
            Assert.Equal(AlarmEvent.None, fifthEvent);
            Assert.Equal(AlarmEvent.Raised, sixthEvent);
        }

        [Fact]
        public void Update_FiveNormalValuesWhileActive_ClearsOnlyOnce()
        {
            ThresholdAlarmDetector detector = new ThresholdAlarmDetector();
            
            // First, raise the alarm
            detector.Update(101f, 100f);
            detector.Update(102f, 100f);
            detector.Update(103f, 100f);

            // Now, provide five normal values
            AlarmEvent fourthEvent = detector.Update(90f, 100f);
            AlarmEvent fifthEvent = detector.Update(95f, 100f);
            AlarmEvent sixthEvent = detector.Update(100f, 100f);
            AlarmEvent seventhEvent = detector.Update(95f, 100f);
            AlarmEvent eighthEvent = detector.Update(90f, 100f);


            Assert.Equal(AlarmEvent.None, fourthEvent);
            Assert.Equal(AlarmEvent.None, fifthEvent);
            Assert.Equal(AlarmEvent.Cleared, sixthEvent);
            Assert.Equal(AlarmEvent.None, seventhEvent);
            Assert.Equal(AlarmEvent.None, eighthEvent);
        }

        [Fact]
        public void Update_ExceedancesAfterClear_RaisesAgainOnThird()
        {
            ThresholdAlarmDetector detector = new ThresholdAlarmDetector();
            
            // First, raise the alarm
            AlarmEvent firstEvent = detector.Update(101f, 100f);
            AlarmEvent secondEvent = detector.Update(102f, 100f);
            AlarmEvent thirdEvent = detector.Update(103f, 100f);
            
            // Clear the alarm
            AlarmEvent fourthEvent = detector.Update(90f, 100f);
            AlarmEvent fifthEvent = detector.Update(95f, 100f);
            AlarmEvent sixthEvent = detector.Update(100f, 100f);

            // Now, provide three more exceedances
            AlarmEvent seventhEvent = detector.Update(101f, 100f);
            AlarmEvent eighthEvent = detector.Update(102f, 100f);
            AlarmEvent ninthEvent = detector.Update(103f, 100f);

            Assert.Equal(AlarmEvent.None, firstEvent);
            Assert.Equal(AlarmEvent.None, secondEvent);
            Assert.Equal(AlarmEvent.Raised, thirdEvent);
            Assert.Equal(AlarmEvent.None, fourthEvent);
            Assert.Equal(AlarmEvent.None, fifthEvent);
            Assert.Equal(AlarmEvent.Cleared, sixthEvent);
            Assert.Equal(AlarmEvent.None, seventhEvent);
            Assert.Equal(AlarmEvent.None, eighthEvent);
            Assert.Equal(AlarmEvent.Raised, ninthEvent);
        }

        [Fact]
        public void Update_SeparateDetectors_KeepIndependentCounts()
        {
            ThresholdAlarmDetector detector_A = new ThresholdAlarmDetector();
            ThresholdAlarmDetector detector_B = new ThresholdAlarmDetector();

            // Detector A: Exceedances
            AlarmEvent dA_firstEvent = detector_A.Update(101f, 100f);
            AlarmEvent dA_secondEvent = detector_A.Update(102f, 100f);
            AlarmEvent dB_firstEvent = detector_B.Update(101f, 100f);
            AlarmEvent dA_thirdEvent = detector_A.Update(103f, 100f);

            // Detector B: Exceedances
            AlarmEvent dB_secondEvent = detector_B.Update(102f, 100f);
            AlarmEvent dB_thirdEvent = detector_B.Update(103f, 100f);

            Assert.Equal(AlarmEvent.None, dA_firstEvent);
            Assert.Equal(AlarmEvent.None, dA_secondEvent);
            Assert.Equal(AlarmEvent.Raised, dA_thirdEvent);

            Assert.Equal(AlarmEvent.None, dB_firstEvent);
            Assert.Equal(AlarmEvent.None, dB_secondEvent);
            Assert.Equal(AlarmEvent.Raised, dB_thirdEvent);
        }

        [Fact]
        public void Update_NaNDuringRecovery_PreservesNormalCount()
        {
            ThresholdAlarmDetector detector = new ThresholdAlarmDetector();

            // First, raise the alarm
            detector.Update(101f, 100f);
            detector.Update(102f, 100f);
            detector.Update(103f, 100f);

            // Now, provide two normal values and a NaN
            AlarmEvent firstEvent = detector.Update(90f, 100f);
            AlarmEvent secondEvent = detector.Update(95f, 100f);
            AlarmEvent thirdEvent = detector.Update(float.NaN, 100f);
            AlarmEvent fourthEvent = detector.Update(100f, 100f);

            Assert.Equal(AlarmEvent.None, firstEvent);
            Assert.Equal(AlarmEvent.None, secondEvent);
            Assert.Equal(AlarmEvent.None, thirdEvent);
            Assert.Equal(AlarmEvent.Cleared, fourthEvent);
        }

        [Fact]
        public void Update_NaNDuringExceedances_PreservesExceededCount()
        {
            ThresholdAlarmDetector detector = new ThresholdAlarmDetector();

            AlarmEvent firstEvent = detector.Update(101f, 100f);
            AlarmEvent secondEvent = detector.Update(102f, 100f);
            AlarmEvent thirdEvent = detector.Update(float.NaN, 100f);
            AlarmEvent fourthEvent = detector.Update(103f, 100f);

            Assert.Equal(AlarmEvent.None, firstEvent);
            Assert.Equal(AlarmEvent.None, secondEvent);
            Assert.Equal(AlarmEvent.None, thirdEvent);
            Assert.Equal(AlarmEvent.Raised, fourthEvent);
        }

        [Fact]
        public void Update_PositiveInfinityDuringExceedances_PreservesExceededCount()
        {
            ThresholdAlarmDetector detector = new ThresholdAlarmDetector();

            AlarmEvent firstEvent = detector.Update(101f, 100f);
            AlarmEvent secondEvent = detector.Update(102f, 100f);
            AlarmEvent thirdEvent = detector.Update(float.PositiveInfinity, 100f);
            AlarmEvent fourthEvent = detector.Update(103f, 100f);

            Assert.Equal(AlarmEvent.None, firstEvent);
            Assert.Equal(AlarmEvent.None, secondEvent);
            Assert.Equal(AlarmEvent.None, thirdEvent);
            Assert.Equal(AlarmEvent.Raised, fourthEvent);
        }

        [Fact]
        public void Update_NegativeInfinityDuringExceedances_PreservesExceededCount()
        {
            ThresholdAlarmDetector detector = new ThresholdAlarmDetector();

            AlarmEvent firstEvent = detector.Update(101f, 100f);
            AlarmEvent secondEvent = detector.Update(102f, 100f);
            AlarmEvent thirdEvent = detector.Update(float.NegativeInfinity, 100f);
            AlarmEvent fourthEvent = detector.Update(103f, 100f);

            Assert.Equal(AlarmEvent.None, firstEvent);
            Assert.Equal(AlarmEvent.None, secondEvent);
            Assert.Equal(AlarmEvent.None, thirdEvent);
            Assert.Equal(AlarmEvent.Raised, fourthEvent);
        }

        [Fact]
        public void ValidatedMeasurement_InvalidStatus_StillRaisesAlarm()
        {
            ThresholdAlarmDetector detector = new ThresholdAlarmDetector();


            SampleValidationResult sample = SensorValueValidator.ValidateSample(150f, 0f, 200f, 3); // Invalid status code

            Assert.Equal(150f, sample.Measurement);
            Assert.Null(sample.StatusCode);
            Assert.True(sample.IsInvalid);


            AlarmEvent firstEvent = SampleAlarmProcessor.Process(sample, detector, 100f);
            AlarmEvent secondEvent = SampleAlarmProcessor.Process(sample, detector, 100f);
            AlarmEvent thirdEvent = SampleAlarmProcessor.Process(sample, detector, 100f);

            Assert.Equal(AlarmEvent.None, firstEvent);
            Assert.Equal(AlarmEvent.None, secondEvent);
            Assert.Equal(AlarmEvent.Raised, thirdEvent);

        }

        [Fact]
        public void ValidatedMeasurement_OutOfRange_PreservesExceededCount()
        {
            ThresholdAlarmDetector detector = new ThresholdAlarmDetector();

            SampleValidationResult firstsample = SensorValueValidator.ValidateSample(150f, 0f, 200f, 1);
            SampleValidationResult secondsample = SensorValueValidator.ValidateSample(160f, 0f, 200f, 1); 
            SampleValidationResult invalidSample = SensorValueValidator.ValidateSample(250f, 0f, 200f, 1);  // Out of range
            SampleValidationResult lastSample = SensorValueValidator.ValidateSample(170f, 0f, 200f, 1);



            List<SampleValidationResult> samples = new List<SampleValidationResult> { firstsample, secondsample, invalidSample, lastSample };
            List<AlarmEvent> actualEvents = new List<AlarmEvent>();
            samples.ForEach(sample => actualEvents.Add(SampleAlarmProcessor.Process(sample, detector, 100f)));

            List<AlarmEvent> expectedEvents = new List<AlarmEvent> { AlarmEvent.None, AlarmEvent.None, AlarmEvent.None, AlarmEvent.Raised };

            Assert.Equal(expectedEvents, actualEvents);

        }
    }
}
