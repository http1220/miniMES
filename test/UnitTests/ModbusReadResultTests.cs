using System;
using Core;
using Modbus;
using Xunit;

namespace UnitTests
{
    public class ModbusReadResultTests
    {
        [Theory]
        [InlineData(true, null, false)]
        [InlineData(false, ModbusFailureType.Timeout, false)]
        [InlineData(false, ModbusFailureType.DeviceException, true)]
        public void Constructor_ValidLegacyArguments_PreservesValuesWithoutReceivedAt(
            bool hasData, ModbusFailureType? failureType, bool hasExceptionCode)
        {
            byte[] data = hasData ? new byte[] { 1, 2 } : null;
            byte? exceptionCode = hasExceptionCode ? (byte?)4 : null;

            var result = new ModbusReadResult(data, failureType, exceptionCode);

            Assert.Same(data, result.Data);
            Assert.Equal(failureType, result.FailureType);
            Assert.Equal(exceptionCode, result.ExceptionCode);
            Assert.Null(result.ReceivedAt);
        }

        [Theory]
        [InlineData(true, ModbusFailureType.ConnectionLost, false)]
        [InlineData(false, null, false)]
        [InlineData(false, ModbusFailureType.DeviceException, false)]
        [InlineData(false, ModbusFailureType.Timeout, true)]
        [InlineData(true, null, true)]
        public void Constructor_InvalidLegacyArguments_Throws(
            bool hasData, ModbusFailureType? failureType, bool hasExceptionCode)
        {
            byte[] data = hasData ? new byte[] { 1, 2 } : null;
            byte? exceptionCode = hasExceptionCode ? (byte?)4 : null;

            Assert.Throws<ArgumentException>(() => new ModbusReadResult(data, failureType, exceptionCode));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Constructor_SuccessWithOptionalReceivedAt_PreservesReceivedAt(bool hasReceivedAt)
        {
            TimeSpan? receivedAt = hasReceivedAt ? (TimeSpan?)TimeSpan.FromSeconds(1) : null;

            var result = new ModbusReadResult(new byte[] { 1, 2 }, null, null, receivedAt);

            Assert.Equal(receivedAt, result.ReceivedAt);
        }

        [Fact]
        public void Constructor_FailureWithReceivedAt_Throws()
        {
            Assert.Throws<ArgumentException>("receivedAt", () =>
                new ModbusReadResult(null, ModbusFailureType.Timeout, null, TimeSpan.Zero));
        }
    }
}
