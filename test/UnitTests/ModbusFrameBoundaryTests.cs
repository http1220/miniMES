using Xunit; 
using Modbus;
namespace UnitTests
{
    public class ModbusFrameBoundaryTests
    {
        [Fact]
        public void GetCompleteFrameLength_EmptyBuffer_ReturnsNull()
        {
            byte[] buffer = new byte[]{};
            Assert.Null(ModbusFrameBoundary.GetCompleteFrameLength(buffer));
        }

        [Fact]
        public void GetCompleteFrameLength_FewerThanSixBytes_ReturnsNull()
        {
            byte[] buffer = new byte[5];
            Assert.Null(ModbusFrameBoundary.GetCompleteFrameLength(buffer));
        }

        [Fact]
        public void GetCompleteFrameLength_IncompleteBody_ReturnsNull()
        {
            byte[] buffer = new byte[8];

            buffer[4] = 0;
            buffer[5] = 3;

            Assert.Null(ModbusFrameBoundary.GetCompleteFrameLength(buffer));
        }

        [Fact]
        public void GetCompleteFrameLength_ExactFrame_ReturnsFrameLength()
        {
            byte[] buffer = new byte[9];

            buffer[4] = 0;
            buffer[5] = 3;

            Assert.Equal(9,ModbusFrameBoundary.GetCompleteFrameLength(buffer));
        }

        [Fact]
        public void GetCompleteFrameLength_TwoFrames_ReturnsFirstFrameLength()
        {
            byte[] buffer = new byte[18];

            buffer[4] = 0;
            buffer[5] = 3;
            buffer[13] = 0;
            buffer[14] = 3;
            
            Assert.Equal(9,ModbusFrameBoundary.GetCompleteFrameLength(buffer));

        }
    }

    
}