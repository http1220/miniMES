using Xunit;
using Core;
using System;
using System.Text;

namespace UnitTests
{
    public class ConnectionBackoffTests
    {
        [Fact]
        public void OnConnectionFailed_FirstFailure_ReturnsOneSecond()
        {
            ConnectionBackoff connection = new ConnectionBackoff();

            TimeSpan result = connection.OnConnectionFailed();

            Assert.Equal(TimeSpan.FromSeconds(1) , result);
        }

        [Fact]

        public void OnConnectionFailed_RepeatedFailures_DoublesUpToTenSeconds()
        {
            ConnectionBackoff connection = new ConnectionBackoff();

            TimeSpan[] Assert_results = new TimeSpan[]{TimeSpan.FromSeconds(1)
                ,TimeSpan.FromSeconds(2)
                ,TimeSpan.FromSeconds(4)
                ,TimeSpan.FromSeconds(8)
                ,TimeSpan.FromSeconds(10)
                ,TimeSpan.FromSeconds(10)};

            foreach(TimeSpan t in Assert_results)
            {
            Assert.Equal(t, connection.OnConnectionFailed());
            }
        }


        [Fact]
        public void OnConnectionSucceeded_ResetsDelayForNextFailure()
        {
            ConnectionBackoff connection = new ConnectionBackoff();

            TimeSpan result =  connection.OnConnectionFailed();

            Assert.Equal(TimeSpan.FromSeconds(1), result);

            result =  connection.OnConnectionFailed();
            Assert.Equal(TimeSpan.FromSeconds(2), result);

            result =  connection.OnConnectionFailed();
            Assert.Equal(TimeSpan.FromSeconds(4), result);

            connection.OnConnectionSucceeded();

            result =  connection.OnConnectionFailed();
            Assert.Equal(TimeSpan.FromSeconds(1), result);

            result =  connection.OnConnectionFailed();
            Assert.Equal(TimeSpan.FromSeconds(2), result);
        }


        [Fact]
        public void ApplyJitter_threetimes()
        {
            ConnectionBackoff connection = new ConnectionBackoff();

            //대기 시간 1초
            TimeSpan result = connection.OnConnectionFailed();
            Assert.Equal(TimeSpan.FromSeconds(1),DelayJitter.ApplyJitter(result,0));

            Assert.Equal(TimeSpan.FromSeconds(1 * 1.2),DelayJitter.ApplyJitter(result,1));

            Assert.Equal(TimeSpan.FromSeconds(1 * 1.1),DelayJitter.ApplyJitter(result,0.5));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(0.25)]
        [InlineData(0.5)]
        [InlineData(0.75)]
        [InlineData(1)]
        public void ApplyJitter_ValidFraction_ReturnsWithinAllowedRange(double ratio)
        {
            TimeSpan baseDelay = TimeSpan.FromSeconds(10);

            TimeSpan actualDelay = DelayJitter.ApplyJitter(baseDelay,ratio);

            Assert.InRange(actualDelay , baseDelay , TimeSpan.FromSeconds(12));
        }

        [Fact]
        public void ApplyJitter_ZeroFraction_PreservesFractionalDelay()
        {
            TimeSpan baseDelay = TimeSpan.FromSeconds(1.5);
            
            Assert.Equal(TimeSpan.FromSeconds(1.5) , DelayJitter.ApplyJitter(baseDelay, 0));
        }
    }    
}
