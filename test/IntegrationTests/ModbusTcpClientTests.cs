using Xunit;
using Modbus;
using System.Threading.Tasks;

namespace IntegrationTests
{
    public class ModbusTcpClientTests
    {
        [Fact]
        public async Task ReadHoldingRegistersAsync_Equipment1_ReturnsExpectedData()
        {
            ModbusTcpClient client = new ModbusTcpClient("127.0.0.1", 15021);
            ModbusReadResponse result = await client.ReadHoldingRegistersAsync(1, 0, 5);
            Assert.Null(result.ExceptionCode);
            byte[] expectedData = new byte[] { 0x41, 0xC8, 0x00, 0x00, 0x3F, 0x80, 0x00, 0x00, 0x00, 0x01 };
            Assert.Equal(expectedData, result.Data);
        }
    }

}