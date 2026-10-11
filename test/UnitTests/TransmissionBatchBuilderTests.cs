using Xunit;
using Core;
using System.Linq;
using System.Collections.Generic;
namespace UnitTests
{
    public class TransmissionBatchBuilderTests
    {
        [Fact]
        public void Build_AlarmsAndSamples_ReturnsAlarmsFirst()
        {
            string[] alarms = new string[] { "A1", "A2" };
            string[] samples = new string[]{
                "S1",
                "S2",
                "S3",
                "S4",
                "S5",
                "S6",
                "S7",
                "S8",
                "S9",
                "S10",
                };


            string[] actualBatch = TransmissionBatchBuilder.Build(alarms, samples, 25);

            string[] expectedItems = alarms.Concat(samples).ToArray();

            Assert.Equal(expectedItems, actualBatch);
        }

        [Fact]
        public void Build_OnlySamplesOverLimit_ReturnsOldestTwentyFive()
        {
            string[] alarms = new string[] { };
            List<string> samples = new List<string>(); ;
            for (int i = 1; i < 31; i++)
            {
                samples.Add($"S{i}");
            }

            string[] expectedItems = samples.Take(25).ToArray();

            string[] actualBatch = TransmissionBatchBuilder.Build(alarms, samples, 25);
            Assert.Equal(30, samples.Count());
            Assert.Equal(expectedItems, actualBatch);

        }

        [Fact]
        public void Build_AlarmsOverLimit_ExcludesSamples()
        {
            List<string> alarms = new List<string>();
            List<string> samples = new List<string>(); ;
            for (int i = 1; i < 31; i++)
            {
                if (i < 11) samples.Add($"S{i}");
                alarms.Add($"A{i}");
            }

            string[] expectedItems = alarms.Take(25).ToArray();

            string[] actualBatch = TransmissionBatchBuilder.Build(alarms, samples, 25);

            Assert.Equal(expectedItems, actualBatch);
        }

        [Fact]
        public void Build_RemainingCapacity_FillsWithOldestSamples()
        {
            List<string> alarms = new List<string>();
            List<string> samples = new List<string>();
            for (int i = 1; i < 21; i++)
            {
                alarms.Add($"A{i}");
            }

            for (int i = 1; i < 11; i++)
            {
                samples.Add($"S{i}");
            }

            string[] expectedSamples = { "S1", "S2", "S3", "S4", "S5" };
            string[] expectedItems = alarms.Concat(expectedSamples).ToArray();

            string[] actualBatch = TransmissionBatchBuilder.Build(alarms, samples, 25);

            Assert.Equal(expectedItems, actualBatch);
        }
    }
}