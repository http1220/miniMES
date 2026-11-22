using System;
using System.Threading;
using System.Threading.Tasks;
using Modbus;

namespace Collector
{
    public sealed class ModbusTcpClientReader : IModbusReader
    {
        private readonly ModbusTcpClient client;

        public ModbusTcpClientReader(string host, int port, Func<TimeSpan> clock)
        {
            client = new ModbusTcpClient(host, port, clock);
        }

        public Task<ModbusReadResult> ReadHoldingRegistersAsync(byte unitId, ushort startAddress, ushort quantity,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            return client.ReadHoldingRegistersAsync(unitId, startAddress, quantity, timeout, cancellationToken);
        }
    }
}
