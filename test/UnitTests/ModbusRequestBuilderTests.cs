using Xunit;
using Modbus;
using System.Buffers.Binary;
using System;
namespace UnitTests
{
    public class ModbusRequestBuilderTests
    {
        [Fact]
        public void BuildReadHoldingRegistersRequest_ValidInputs_ReturnsExpectedBytes()
        {
            // Given
            ushort transactionId = 1;
            byte unitId = 1;
            ushort startAddress = 0;
            ushort quantity = 5;
            byte[] expectedArray = new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x01, 0x03, 0x00, 0x00, 0x00, 0x05 };
            // When
            byte[] result = ModbusRequestBuilder.BuildReadHoldingRegistersRequest(transactionId, unitId, startAddress, quantity);
            // Then
            Assert.Equal(expectedArray, result);
        }

        [Fact]
        public void BuildReadHoldingRegistersRequest_MultiByteValues_UsesBigEndian()
        {
            // Given
            ushort transactionId = 0x1234;
            byte unitId = 1;
            ushort startAddress = 0x5678;
            ushort quantity = 5;
            byte[] expectedArray = new byte[] { 0x12, 0x34, 0x00, 0x00, 0x00, 0x06, 0x01, 0x03, 0x56, 0x78, 0x00, 0x05 };
            // When
            // When
            byte[] result = ModbusRequestBuilder.BuildReadHoldingRegistersRequest(transactionId, unitId, startAddress, quantity);
            // Then
            Assert.Equal(expectedArray, result);
        }

        [Fact]
        public void WriteUInt16BigEndian_MultiByteValue_WritesHighByteFirst()
        {
            // Given

            // ushort는 음수 없는 16비트(2바이트) 정수 자료형이다.
            // 0x는 16진수 표기이며, 0x1234의 상위 바이트는 0x12, 하위 바이트는 0x34이다.
            ushort value = 0x1234;

            // 2바이트를 담을 배열을 만든다. 각 요소의 초기값은 0이다.
            byte[] buffer = new byte[2];

            // Big-endian은 상위 바이트를 먼저 기록하므로 0x12, 0x34 순서를 기대한다.
            byte[] expectedBuffer = new byte[] { 0x12, 0x34 };

            // When

            // AsSpan(0, 2)는 인덱스 0부터 길이 2인 구간을 가리킨다.
            // 두 번째 인자는 끝 인덱스가 아니라 사용할 요소의 개수다.
            // 새 배열을 복사하지 않으므로 이 구간에 쓰면 원본 buffer가 변경된다.
            // WriteUInt16BigEndian은 ushort 값을 상위 바이트부터 2바이트로 기록한다.
            // 직접 >> 8과 & 0xFF로 나눠 담았던 작업을 대신한다.
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), value);

            // Then

            // 두 배열의 내용과 순서가 같은지 확인한다.
            Assert.Equal(expectedBuffer, buffer);
        }
    }
}