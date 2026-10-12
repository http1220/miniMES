using Xunit;
using Modbus;
using Xunit.Sdk;
using System;

namespace UnitTests
{
    public class ModbusResponseParserTests
    {
        [Fact]
        public void ParseReadHoldingRegistersResponse_NormalResponse_ReturnsData()
        {
            // Given
            byte[] buffer = new byte[]
            {
                0x00, 0x01,             // 트랜잭션 ID
                0x00, 0x00,             // 프로토콜 ID
                0x00, 0x07,             // 뒤에 오는 길이: 7바이트
                0x01,                   // 유닛 ID
                0x03,                   // 함수코드: 정상 읽기 응답
                0x04,                   // 데이터 바이트 개수: 4
                0x12, 0x34, 0x56, 0x78  // 레지스터 2개의 데이터
            };
            byte[] expectedArray = new byte[] { 0x12, 0x34, 0x56, 0x78 };
            // When
            ModbusReadResponse result = ModbusResponseParser.ParseReadHoldingRegistersResponse(buffer);
            // Then
            Assert.NotNull(result);
            Assert.Equal(expectedArray , result.Data);
            Assert.Null(result.ExceptionCode);
        }

        [Theory]
        [InlineData(0x02)]
        [InlineData(0x01)]
        public void ParseReadHoldingRegistersResponse_ExceptionResponse_ReturnsExceptionCode(byte ex)
        {
            // Given
            byte[] buffer = new byte[]
            {
                0x00, 0x01,             // 트랜잭션 ID
                0x00, 0x00,             // 프로토콜 ID
                0x00, 0x03,             // 뒤에 오는 길이: 3바이트
                0x01,                   // 유닛 ID
                0x83,                   // 함수
                ex                    // 예외 코드
            };
        
            // When
            ModbusReadResponse result = ModbusResponseParser.ParseReadHoldingRegistersResponse(buffer);

            // Then
            Assert.NotNull(result);
            Assert.Null(result.Data);
            Assert.Equal(ex , result.ExceptionCode.Value);
        }

        [Fact]
        public void IsTransactionIdMatch_SameId_ReturnsTrue()
        {
            // Given
            byte[] given = new byte[]{0x12,0x34};
            // When
            bool result = ModbusResponseParser.IsTransactionIdMatch(given , 0x1234);
            // Then
            Assert.True(result);
        }

        [Fact]
        public void IsTransactionIdMatch_DifferentId_ReturnsFalse()
        {
            // Given
            byte[] given = new byte[]{0x12,0x34};
            // When
            bool result = ModbusResponseParser.IsTransactionIdMatch(given , 0x1235);
            // Then
            Assert.False(result);
        }

        [Fact]
        public void ParseReadHoldingRegistersResponse_UnexpectedFunctionCode_Throws()
        {
            // Given
            byte[] buffer = new byte[]
            {
                0x00, 0x01,             // 트랜잭션 ID
                0x00, 0x00,             // 프로토콜 ID
                0x00, 0x07,             // 뒤에 오는 길이: 7바이트
                0x01,                   // 유닛 ID
                0x04,                   // 함수코드: 정상 읽기 응답
                0x04,                   // 이 파서가 지원하지 않는 함수코드
                0x12, 0x34, 0x56, 0x78  // 레지스터 2개의 데이터
            };
            // When
            // Then
            Assert.Throws<ArgumentException>( () => ModbusResponseParser.ParseReadHoldingRegistersResponse(buffer));
        }

        [Fact]
        public void ParseReadHoldingRegistersResponse_TooShortFrame_Throws()
        {
            // Given
            byte[] frame = new byte[8];
            frame[7] = 0x03;

            // When
        
            Assert.Throws<ArgumentException>( () => ModbusResponseParser.ParseReadHoldingRegistersResponse(frame));
            // Then
        }

        [Fact]
        public void ParseReadHoldingRegistersResponse_IncompleteData_Throws()
        {
            // Given
            byte[] buffer = new byte[]
            {
                0x00, 0x01,       // 트랜잭션 ID
                0x00, 0x00,       // 프로토콜 ID
                0x00, 0x07,       // 뒤에 7바이트가 와야 한다고 선언
                0x01,             // 유닛 ID
                0x03,             // 함수코드
                0x04,             // 데이터는 4바이트라고 선언
                0x12, 0x34, 0x56  // 실제 데이터는 3바이트만 있음
            };
            // When
            ArgumentException ex = Assert.Throws<ArgumentException>( () => ModbusResponseParser.ParseReadHoldingRegistersResponse(buffer));
        
            // Then
            Assert.Equal("frame" , ex.ParamName);
        }
    }
}