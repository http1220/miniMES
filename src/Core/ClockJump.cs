using System;

namespace Core
{
    public sealed class ClockJump
    {
        public TimeSpan Delta { get; }

        public ClockJump(TimeSpan delta)
        {
            Delta = delta;
        }
    }
}
