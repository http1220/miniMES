using Xunit;
using Core;

namespace UnitTests
{
    public class RegisterFloatDecoderTests
    {
        [Fact]
        public void Decode_ABCDRegisters_Returns123_45()
        {
            
            ushort firstRegister = 0x42F6;
            ushort secondRegister = 0xE666;

            RegisterWordOrder order = RegisterWordOrder.ABCD;

            float result = RegisterFloatDecoder.Decode(firstRegister , secondRegister , order);

            Assert.Equal(123.45f , result);
            
        }

        [Fact]
        public void Decode_CDABRegisters_Returns123_45()
        {
            ushort firstRegister = 0xE666;
            ushort secondRegister = 0x42F6;
            RegisterWordOrder order = RegisterWordOrder.CDAB;

            float result = RegisterFloatDecoder.Decode(firstRegister, secondRegister , order);

            Assert.Equal(123.45f , result);
        }

        [Fact]
        public void Decode_SameRegistersWithDifferentOrders_ReturnsDifferentValues()
        {
            ushort firstRegister = 0x42F6;
            ushort secondRegister = 0xE666;

            float ABCD_result = RegisterFloatDecoder.Decode(firstRegister , secondRegister , RegisterWordOrder.ABCD);

            float CDAB_result = RegisterFloatDecoder.Decode(firstRegister , secondRegister , RegisterWordOrder.CDAB);

            Assert.NotEqual(ABCD_result,CDAB_result);
        }
    }
}