using System;

namespace Core
{
    
    public class DelayJitter
    {
        public static TimeSpan ApplyJitter(TimeSpan baseDelay, double randomFraction)
        {
            return TimeSpan.FromSeconds(baseDelay.TotalSeconds * (1 + 0.2 * randomFraction));
        }
    }
}