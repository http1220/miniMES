using System;
using System.Linq;
using Core;
using Xunit;

namespace UnitTests
{
    public class FaultStateRulesTests
    {
        private static readonly DateTime Raised = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc);
        private static readonly FaultStatus[] States = { FaultStatus.Registered, FaultStatus.InProgress, FaultStatus.Closed, FaultStatus.Cancelled };

        private static FaultTransitionCheckResult Check(FaultStatus current, FaultStatus next, string reason, DateTime? recovered)
        {
            return FaultStateRules.ValidateTransition(current, next, reason, recovered, Raised, Now);
        }

        [Theory]
        [InlineData(FaultStatus.Registered, FaultStatus.InProgress)]
        [InlineData(FaultStatus.InProgress, FaultStatus.Closed)]
        [InlineData(FaultStatus.Registered, FaultStatus.Cancelled)]
        [InlineData(FaultStatus.InProgress, FaultStatus.Cancelled)]
        [InlineData(FaultStatus.Closed, FaultStatus.InProgress)]
        [InlineData(FaultStatus.Cancelled, FaultStatus.Registered)]
        public void ValidateTransition_AllowedPairs_ReturnsOk(FaultStatus current, FaultStatus next)
        {
            Assert.Equal(FaultTransitionCheckResult.Ok, Check(current, next, "reason", next == FaultStatus.Closed ? Raised.AddMinutes(30) : (DateTime?)null));
        }

        [Theory]
        [InlineData(FaultStatus.Registered, FaultStatus.Registered)]
        [InlineData(FaultStatus.Registered, FaultStatus.Closed)]
        [InlineData(FaultStatus.InProgress, FaultStatus.Registered)]
        [InlineData(FaultStatus.InProgress, FaultStatus.InProgress)]
        [InlineData(FaultStatus.Closed, FaultStatus.Registered)]
        [InlineData(FaultStatus.Closed, FaultStatus.Closed)]
        [InlineData(FaultStatus.Closed, FaultStatus.Cancelled)]
        [InlineData(FaultStatus.Cancelled, FaultStatus.InProgress)]
        [InlineData(FaultStatus.Cancelled, FaultStatus.Closed)]
        [InlineData(FaultStatus.Cancelled, FaultStatus.Cancelled)]
        public void ValidateTransition_DisallowedPairs_ReturnsInvalidTransition(FaultStatus current, FaultStatus next)
        {
            Assert.Equal(FaultTransitionCheckResult.InvalidTransition, Check(current, next, "reason", null));
            Assert.Equal(FaultTransitionCheckResult.InvalidTransition, Check(current, next, "reason", Now));
        }

        [Theory]
        [InlineData(FaultStatus.Registered, FaultStatus.Cancelled)]
        [InlineData(FaultStatus.InProgress, FaultStatus.Cancelled)]
        [InlineData(FaultStatus.Closed, FaultStatus.InProgress)]
        [InlineData(FaultStatus.Cancelled, FaultStatus.Registered)]
        public void ValidateTransition_RequiredReasonMissing_ReturnsReasonRequired(FaultStatus current, FaultStatus next)
        {
            foreach (var reason in new[] { null, "", "   " })
                Assert.Equal(FaultTransitionCheckResult.ReasonRequired, Check(current, next, reason, null));
        }

        [Theory]
        [InlineData(FaultStatus.Registered, FaultStatus.InProgress)]
        [InlineData(FaultStatus.InProgress, FaultStatus.Closed)]
        public void ValidateTransition_OptionalReasonMissing_ReturnsOk(FaultStatus current, FaultStatus next)
        {
            Assert.Equal(FaultTransitionCheckResult.Ok, Check(current, next, null, next == FaultStatus.Closed ? Now : (DateTime?)null));
        }

        [Fact]
        public void ValidateTransition_ClosedWithoutRecoveryTime_ReturnsInvalidRecoveryTime()
        {
            Assert.Equal(FaultTransitionCheckResult.InvalidRecoveryTime, Check(FaultStatus.InProgress, FaultStatus.Closed, null, null));
        }

        [Fact]
        public void ValidateTransition_RecoveryTimeAtBoundaries_ReturnsOk()
        {
            foreach (var recovered in new[] { Raised, Now })
                Assert.Equal(FaultTransitionCheckResult.Ok, Check(FaultStatus.InProgress, FaultStatus.Closed, null, recovered));
        }

        [Fact]
        public void ValidateTransition_RecoveryTimeOutsideBoundaries_ReturnsInvalidRecoveryTime()
        {
            foreach (var recovered in new[] { Raised.AddMilliseconds(-1), Now.AddMilliseconds(1) })
                Assert.Equal(FaultTransitionCheckResult.InvalidRecoveryTime, Check(FaultStatus.InProgress, FaultStatus.Closed, null, recovered));
        }

        [Theory]
        [InlineData(FaultStatus.Registered, FaultStatus.InProgress)]
        [InlineData(FaultStatus.Registered, FaultStatus.Cancelled)]
        [InlineData(FaultStatus.InProgress, FaultStatus.Cancelled)]
        [InlineData(FaultStatus.Closed, FaultStatus.InProgress)]
        [InlineData(FaultStatus.Cancelled, FaultStatus.Registered)]
        public void ValidateTransition_NonClosedWithRecoveryTime_ReturnsInvalidRecoveryTime(FaultStatus current, FaultStatus next)
        {
            Assert.Equal(FaultTransitionCheckResult.InvalidRecoveryTime, Check(current, next, "reason", Now));
        }

        [Fact]
        public void ValidateTransition_MultipleErrors_ReturnsFirstError()
        {
            Assert.Equal(FaultTransitionCheckResult.InvalidTransition, Check(FaultStatus.Registered, FaultStatus.Closed, null, Now));
            Assert.Equal(FaultTransitionCheckResult.ReasonRequired, Check(FaultStatus.Registered, FaultStatus.Cancelled, null, Now));
        }

        [Fact]
        public void AllowedTargets_KnownStates_ReturnsOrderedTargets()
        {
            Assert.Equal(new[] { FaultStatus.InProgress, FaultStatus.Cancelled }, FaultStateRules.AllowedTargets(FaultStatus.Registered));
            Assert.Equal(new[] { FaultStatus.Closed, FaultStatus.Cancelled }, FaultStateRules.AllowedTargets(FaultStatus.InProgress));
            Assert.Equal(new[] { FaultStatus.InProgress }, FaultStateRules.AllowedTargets(FaultStatus.Closed));
            Assert.Equal(new[] { FaultStatus.Registered }, FaultStateRules.AllowedTargets(FaultStatus.Cancelled));
        }

        [Fact]
        public void AllowedTargets_AllStatePairs_AgreesWithValidation()
        {
            foreach (var current in States)
            foreach (var next in States)
                Assert.Equal(FaultStateRules.AllowedTargets(current).Contains(next),
                    Check(current, next, "reason", next == FaultStatus.Closed ? Now : (DateTime?)null) != FaultTransitionCheckResult.InvalidTransition);
        }

        [Theory]
        [InlineData(FaultStatus.Registered, true)]
        [InlineData(FaultStatus.InProgress, true)]
        [InlineData(FaultStatus.Closed, false)]
        [InlineData(FaultStatus.Cancelled, true)]
        public void CanEditTexts_KnownStates_ReturnsExpected(FaultStatus status, bool expected)
        {
            Assert.Equal(expected, FaultStateRules.CanEditTexts(status));
        }

        [Theory]
        [InlineData((FaultStatus)0)]
        [InlineData((FaultStatus)5)]
        public void Rules_UndefinedStatus_RejectsStatus(FaultStatus undefined)
        {
            Assert.Empty(FaultStateRules.AllowedTargets(undefined));
            Assert.False(FaultStateRules.CanEditTexts(undefined));
            foreach (var status in States.Concat(new[] { undefined }))
            {
                Assert.Equal(FaultTransitionCheckResult.InvalidTransition, Check(undefined, status, "reason", null));
                Assert.Equal(FaultTransitionCheckResult.InvalidTransition, Check(status, undefined, "reason", null));
            }
        }

        [Fact]
        public void AllowedTargets_ModifiedReturnedArray_DoesNotAffectNextCall()
        {
            var targets = (FaultStatus[])FaultStateRules.AllowedTargets(FaultStatus.Registered);
            targets[0] = FaultStatus.Closed;
            Assert.Equal(new[] { FaultStatus.InProgress, FaultStatus.Cancelled }, FaultStateRules.AllowedTargets(FaultStatus.Registered));
        }
    }
}
