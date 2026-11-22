using System;

namespace Core
{
    
    public class DelayJitter
    {
        public static TimeSpan ApplyJitter(TimeSpan baseDelay, double randomFraction)
        {
            return ApplyJitter(baseDelay, randomFraction, 0.2);
        }

        public static TimeSpan ApplyJitter(TimeSpan baseDelay, double randomFraction, double maximumJitterFraction)
        {
            if (maximumJitterFraction < 0)
                throw new ArgumentOutOfRangeException(nameof(maximumJitterFraction));
            return TimeSpan.FromSeconds(baseDelay.TotalSeconds * (1 + maximumJitterFraction * randomFraction));
        }
    }
}