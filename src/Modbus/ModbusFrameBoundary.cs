namespace Modbus
{
    public class ModbusFrameBoundary
    {
        public static int? GetCompleteFrameLength(byte[] buffer)
        {
            if(buffer.Length < 6) {return null;}

            int remainingLength = (buffer[4] << 8) | buffer[5];

            int frameLength = remainingLength + 6;

            
            if(buffer.Length < frameLength) {return null;}
            else {return frameLength;}
        }
    }
}