using System;

namespace Core
{
    public class ConnectionBackoff
    {
        private readonly TimeSpan maximumDelay;

        public ConnectionBackoff() : this(TimeSpan.FromSeconds(10))
        {
        }

        public ConnectionBackoff(TimeSpan maximumDelay)
        {
            if (maximumDelay < TimeSpan.FromSeconds(1))
                throw new ArgumentOutOfRangeException(nameof(maximumDelay));
            this.maximumDelay = maximumDelay;
        }

        private TimeSpan? previousDelay;

        public TimeSpan OnConnectionFailed()
        {
            //이전 대기 시간이 있음.
            if(previousDelay.HasValue)
            {
                var ticks = previousDelay.Value.Ticks;
                previousDelay = ticks > maximumDelay.Ticks / 2
                    ? maximumDelay
                    : TimeSpan.FromTicks(ticks * 2);
            }
            else //아직 없음
            {
                previousDelay = TimeSpan.FromSeconds(1);
            }

            return previousDelay.Value;
        }

        public void OnConnectionSucceeded()
        {
            previousDelay = null;
        }



    }
}

