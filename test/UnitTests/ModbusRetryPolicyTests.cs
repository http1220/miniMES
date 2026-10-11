using Xunit;
using Core;
namespace UnitTests
{
    public class ModbusRetryPolicyTests
    {
        [Theory]
        [InlineData(ModbusFailureType.ConnectionFailed,true)]
        [InlineData(ModbusFailureType.ConnectionLost,true)]
        [InlineData(ModbusFailureType.DeviceException,false)]
        [InlineData(ModbusFailureType.InvalidValue,false)]
        [InlineData(ModbusFailureType.Timeout,false)]
        public void ShouldReconnect_ReturnsExpectedResult(ModbusFailureType failureType, bool expected)
        {
            
            Assert.Equal(expected , ModbusRetryPolicy.ShouldReconnect(failureType));
        }
    }
}