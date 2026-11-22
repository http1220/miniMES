using System;
using System.Threading;
using System.Threading.Tasks;
using Modbus;

namespace Collector
{
    public interface IModbusReader
    {
        Task<ModbusReadResult> ReadHoldingRegistersAsync(byte unitId, ushort startAddress, ushort quantity,
            TimeSpan timeout, CancellationToken cancellationToken);
    }
}
