using System;

namespace Core
{
    public sealed class ClockReference
    {
        private readonly TimeSpan jumpThreshold;
        private TimeSpan referenceMonotonic;
        private DateTime? referenceUtc;

        public bool HasReference => referenceUtc.HasValue;

        public ClockReference(TimeSpan jumpThreshold)
        {
            if (jumpThreshold <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(jumpThreshold));
            this.jumpThreshold = jumpThreshold;
        }

        public ClockJump Update(TimeSpan receivedAtMonotonic, DateTime serverUtc)
        {
            if (serverUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Server time must be UTC.", nameof(serverUtc));
            if (HasReference && receivedAtMonotonic <= referenceMonotonic)
                return null;

            var delta = TryToUtc(receivedAtMonotonic, out var projected)
                ? serverUtc - projected
                : TimeSpan.Zero;

            referenceMonotonic = receivedAtMonotonic;
            referenceUtc = serverUtc;

            return delta.Duration() >= jumpThreshold ? new ClockJump(delta) : null;
        }

        public bool TryToUtc(TimeSpan monotonic, out DateTime utc)
        {
            utc = HasReference ? referenceUtc.Value + (monotonic - referenceMonotonic) : default(DateTime);
            return HasReference;
        }
    }
}
