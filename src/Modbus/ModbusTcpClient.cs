using System;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Modbus
{
    public class ModbusTcpClient
    {
        private readonly string _host;
        private readonly int _port;

        //표준 Modbus TCP 최대 길이는 PDU 253바이트와 MBAP 헤더 7바이트를 합한 260바이트입니다.
        private const int MaxModbusTcpFrameLength = 260;
        public ModbusTcpClient(string hostname, int port)
        {
            _host = hostname;
            _port = port;

        }

        public async Task<ModbusReadResponse> ReadHoldingRegistersAsync(byte unitId , ushort startAddress , ushort quantity)
        {
            TcpClient tcpClient = new TcpClient();

            using (tcpClient)
            {
                await tcpClient.ConnectAsync(_host, _port);

                NetworkStream networkStream = tcpClient.GetStream();
                ushort transactionId = 1;
                byte[] request = ModbusRequestBuilder.BuildReadHoldingRegistersRequest(transactionId, unitId, startAddress, quantity);
                
                await networkStream.WriteAsync(request, 0, request.Length);


                byte[] buffer = new byte[MaxModbusTcpFrameLength];
                int receivedCount = 0;
                int? frameLength;
                while(true)
                {
                    if(buffer.Length - receivedCount == 0) throw new InvalidOperationException();
                    int bytesRead = await networkStream.ReadAsync(buffer, receivedCount, buffer.Length - receivedCount);
                    if(bytesRead == 0) throw new System.IO.EndOfStreamException();
                    receivedCount += bytesRead;
                    byte[] result = new byte[receivedCount];
                    Array.Copy(buffer , result , receivedCount);
                    frameLength = ModbusFrameBoundary.GetCompleteFrameLength(result);
                    if(frameLength.HasValue) break;
                }

                byte[] frame = new byte[frameLength.Value];

                Array.Copy(buffer,frame,frameLength.Value);
                ModbusReadResponse response = ModbusResponseParser.ParseReadHoldingRegistersResponse(frame);

                bool isTransactionIdMatch = ModbusResponseParser.IsTransactionIdMatch(frame , transactionId);

                if (!isTransactionIdMatch)
                {
                    throw new InvalidOperationException("요청과 응답의 Transaction ID가 다릅니다.");
                }

                if (response.ExceptionCode.HasValue)
                {
                    throw new InvalidOperationException($"장비가 Modbus 예외 응답을 반환했습니다. {response.ExceptionCode.Value}");
                }

                return response;
            }

        }
    }
}