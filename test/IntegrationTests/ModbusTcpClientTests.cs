using System;
using System.Threading.Tasks;
using Core;
using Modbus;
using Xunit;

namespace IntegrationTests
{
    public class ModbusTcpClientTests
    {
        [Fact]
        public async Task ReadHoldingRegistersAsync_Equipment1_ReturnsExpectedData()
        {
            ModbusTcpClient client = new ModbusTcpClient("127.0.0.1", 15021);
            ModbusReadResult result = await client.ReadHoldingRegistersAsync(1, 0, 5, TimeSpan.FromSeconds(2));
            Assert.Null(result.ExceptionCode);
            Assert.Null(result.FailureType);
            byte[] expectedData = new byte[] { 0x41, 0xC8, 0x00, 0x00, 0x3F, 0x80, 0x00, 0x00, 0x00, 0x01 };
            Assert.Equal(expectedData, result.Data);
        }

        [Fact]
        public async Task ReadHoldingRegistersAsync_DelayedResponse_ReturnsTimeout()
        {
            //delay 1 5000
            ModbusTcpClient client = new ModbusTcpClient("127.0.0.1", 15021);
            ModbusReadResult result = await client.ReadHoldingRegistersAsync(1, 0, 5, TimeSpan.FromSeconds(2));
            Assert.Null(result.ExceptionCode);
            Assert.Equal(ModbusFailureType.Timeout, result.FailureType);
            Assert.Null(result.Data);

        }

        [Fact]
        public async Task ReadHoldingRegistersAsync_DisconnectedEquipment_ReturnsConnectionFailed()
        {
            ModbusTcpClient client = new ModbusTcpClient("127.0.0.1", 15021);
            ModbusReadResult result = await client.ReadHoldingRegistersAsync(1, 0, 5, TimeSpan.FromSeconds(10));
            Assert.Equal(ModbusFailureType.ConnectionFailed, result.FailureType);
            Assert.Null(result.Data);
            Assert.Null(result.ExceptionCode);
        }

        [Fact]
        public async Task ReadHoldingRegistersAsync_DeviceException_ReturnsDeviceException()
        {
            ModbusTcpClient client = new ModbusTcpClient("127.0.0.1", 15021);
            ModbusReadResult result = await client.ReadHoldingRegistersAsync(1, 0, 5, TimeSpan.FromSeconds(2));
            Assert.Equal(ModbusFailureType.DeviceException, result.FailureType);
            Assert.Null(result.Data);
            Assert.Equal(0x04, result.ExceptionCode.Value);
        }

        [Fact]
        public async Task
        ReadHoldingRegistersAsync_ConnectionClosedDuringRead_ReturnsConnectionLost()
        {
            ModbusTcpClient client = new ModbusTcpClient("127.0.0.1", 15021);
            ModbusReadResult result = await client.ReadHoldingRegistersAsync(1, 0, 5, TimeSpan.FromSeconds(30));
            Assert.Equal(ModbusFailureType.ConnectionLost, result.FailureType);
            Assert.Null(result.Data);
            Assert.Null(result.ExceptionCode);
        }

    }

}