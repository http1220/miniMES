namespace Modbus
{
    public class ModbusRequestBuilder
    {
        public static byte[]BuildReadHoldingRegistersRequest(
            ushort transactionId, byte unitId, ushort startAddress, ushort quantity
        )
        {
            byte[] result = new byte[12];

            result[0] = (byte)(transactionId >> 8);
            result[1] = (byte)(transactionId & 0xff);
            result[2] = 0x00;
            result[3] = 0x00;
            result[4] = 0x00;
            result[5] = 0x06;
            result[6] = unitId;
            result[7] = 0x03;
            result[8] = (byte)(startAddress >> 8);
            result[9] = (byte)(startAddress & 0xff);
            result[10] = (byte)(quantity >> 8);
            result[11] = (byte)(quantity & 0xff);

            return result;
        }
    }
}