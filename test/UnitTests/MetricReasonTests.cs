using System;
using Core;
using Server;
using Xunit;

namespace UnitTests
{
    public sealed class MetricReasonTests
    {
        [Theory]
        [InlineData(MetricUnavailableReason.NoFaults, "no_faults")]
        [InlineData(MetricUnavailableReason.NoClosedFaults, "no_closed_faults")]
        [InlineData(MetricUnavailableReason.ZeroObservedTime, "zero_observed_time")]
        [InlineData(MetricUnavailableReason.StructureError, "structure_error")]
        [InlineData(MetricUnavailableReason.ChildUnavailable, "child_unavailable")]
        public void ReasonName_MapsContractNames(MetricUnavailableReason reason, string expected)
        {
            Assert.Equal(expected, MetricsController.ReasonName(reason));
        }

        [Fact]
        public void ReasonName_RejectsUnknownValue()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => MetricsController.ReasonName((MetricUnavailableReason)99));
        }
    }
}
