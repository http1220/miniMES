using System;

namespace Core
{
    public class ConnectionBackoff
    {
        private TimeSpan? previousDelay;

        public TimeSpan OnConnectionFailed()
        {
            //이전 대기 시간이 있음.
            if(previousDelay.HasValue)
            {
                 double doubleSeconds = previousDelay.Value.TotalSeconds * 2;

                 doubleSeconds = Math.Min(doubleSeconds ,  10);

                 previousDelay = TimeSpan.FromSeconds(doubleSeconds);
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

