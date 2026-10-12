using System;
using System.Buffers.Binary;

namespace Modbus
{
    public class ModbusResponseParser
    {
        public static ModbusReadResponse ParseReadHoldingRegistersResponse(byte[] frame)
        {
            if(frame.Length < 9)
            {
                throw new ArgumentException("지원하지 않는 함수코드입니다.", nameof(frame));
            }

            if(frame[7] != 0x03 && frame[7] != 0x83)
            {
                throw new ArgumentException("지원하지 않는 함수코드입니다.", nameof(frame));
            }

            if((frame[7] & 0x80) != 0)
            {
                return new ModbusReadResponse(null , frame[8]);
            }

            byte length = frame[8];

            if(frame.Length < 9 + length)
            {
                throw new ArgumentException("선언된 데이터 길이보다 프레임이 짧습니다.", nameof(frame));
            }

            byte[] buffer = new byte[length];
            Array.Copy(frame, 9, buffer, 0, length);
            ModbusReadResponse result = new ModbusReadResponse(buffer, null);
            return result;
        }

        /*
        public static bool IsTransactionIdMatch(byte[] frame, ushort expectedTransactionId)
        {
            return ((frame[0] << 8) | frame[1]) == expectedTransactionId;
        }*/
        
        public static bool IsTransactionIdMatch(byte[] frame, ushort expectedTransactionId)
        {
            //frame.AsSpan(0, 2)는 인덱스 0부터 2바이트를 가리키고, API가 이를 big-endian ushort로 읽어요. 직접 작성한 << 8과 |를 대신
        ushort transactionId =
            BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(0, 2));

        return transactionId == expectedTransactionId;
        }
    }
}