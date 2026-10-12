namespace Modbus
{
    public class ModbusReadResponse
    {
        public byte[] Data{get;}
        public byte? ExceptionCode{get;}

        public ModbusReadResponse(byte[] data , byte? exceptionCode)
        {
            this.Data=data;
            this.ExceptionCode=exceptionCode;
        }
    }
}