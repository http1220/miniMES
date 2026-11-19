using System;
using System.Collections.Generic;

namespace Core
{
    public static class FaultStateRules
    {
        private sealed class Transition
        {
            public readonly FaultStatus Current;
            public readonly FaultStatus Next;
            public readonly bool RequiresReason;

            public Transition(FaultStatus current, FaultStatus next, bool requiresReason)
            {
                Current = current;
                Next = next;
                RequiresReason = requiresReason;
            }
        }

        private static readonly Transition[] Transitions =
        {
            new Transition(FaultStatus.Registered, FaultStatus.InProgress, false),
            new Transition(FaultStatus.InProgress, FaultStatus.Closed, false),
            new Transition(FaultStatus.Registered, FaultStatus.Cancelled, true),
            new Transition(FaultStatus.InProgress, FaultStatus.Cancelled, true),
            new Transition(FaultStatus.Closed, FaultStatus.InProgress, true),
            new Transition(FaultStatus.Cancelled, FaultStatus.Registered, true)
        };

        public static IReadOnlyList<FaultStatus> AllowedTargets(FaultStatus current)
        {
            var targets = new List<FaultStatus>();
            foreach (var transition in Transitions)
                if (transition.Current == current) targets.Add(transition.Next);
            return targets.ToArray();
        }

        // All times are assumed to be UTC; their DateTime values are compared as supplied.
        public static FaultTransitionCheckResult ValidateTransition(FaultStatus current, FaultStatus next,
            string reason, DateTime? recoveredAtUtc, DateTime alarmRaisedAtUtc, DateTime nowUtc)
        {
            Transition match = null;
            foreach (var transition in Transitions)
                if (transition.Current == current && transition.Next == next)
                {
                    match = transition;
                    break;
                }

            if (match == null) return FaultTransitionCheckResult.InvalidTransition;
            if (match.RequiresReason && string.IsNullOrWhiteSpace(reason))
                return FaultTransitionCheckResult.ReasonRequired;
            if (next == FaultStatus.Closed)
            {
                if (!recoveredAtUtc.HasValue || recoveredAtUtc.Value < alarmRaisedAtUtc || recoveredAtUtc.Value > nowUtc)
                    return FaultTransitionCheckResult.InvalidRecoveryTime;
            }
            else if (recoveredAtUtc.HasValue)
                return FaultTransitionCheckResult.InvalidRecoveryTime;

            return FaultTransitionCheckResult.Ok;
        }

        public static bool CanEditTexts(FaultStatus status)
        {
            if (status == FaultStatus.Closed) return false;
            foreach (var transition in Transitions)
                if (transition.Current == status) return true;
            return false;
        }
    }
}
