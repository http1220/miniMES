using System;

namespace Core
{
    public class RegisterFloatDecoder
    {
        public static float Decode(ushort firstRegister , ushort secondRegister , RegisterWordOrder wordOrder)
        {
            if(RegisterWordOrder.CDAB==wordOrder)
            {
                (secondRegister, firstRegister) = (firstRegister, secondRegister);
            }
            uint upperBits = (uint)firstRegister << 16;

            uint combinedBits = upperBits | (uint)secondRegister;

            byte[] bytes = BitConverter.GetBytes(combinedBits);


            return BitConverter.ToSingle(bytes , 0);
        }
    }

}