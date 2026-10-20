using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Core;
using Modbus;
using Xunit;

namespace IntegrationTests
{
    // 시뮬레이터는 요청 ID를 그대로 반환하므로, ID 불일치를 주입하는 로컬 서버를 사용합니다.
    public class ModbusTcpClientTransactionIdTests
    {
        private static readonly byte[] ExpectedData =
            { 0x41, 0xC8, 0x00, 0x00, 0x3F, 0x80, 0x00, 0x00, 0x00, 0x01 };

        [Fact]
        public async Task ReadHoldingRegistersAsync_MismatchedThenMatchingFrame_ReturnsMatchingData()
        {
            ModbusReadResult result = await ExchangeAsync(async (stream, transactionId) =>
            {
                byte[] unrelated = NormalFrame((ushort)(transactionId + 1), new byte[] { 0xDE, 0xAD });
                byte[] matching = NormalFrame(transactionId, ExpectedData);
                byte[] combined = new byte[unrelated.Length + matching.Length];
                Array.Copy(unrelated, combined, unrelated.Length);
                Array.Copy(matching, 0, combined, unrelated.Length, matching.Length);
                await stream.WriteAsync(combined, 0, combined.Length);
            });

            Assert.Null(result.FailureType);
            Assert.Null(result.ExceptionCode);
            Assert.Equal(ExpectedData, result.Data);
        }

        [Fact]
        public async Task ReadHoldingRegistersAsync_MismatchedExceptionThenMatchingFrameSentInParts_ReturnsMatchingData()
        {
            ModbusReadResult result = await ExchangeAsync(async (stream, transactionId) =>
            {
                byte[] unrelated = NormalFrame((ushort)(transactionId + 1), new byte[0]);
                unrelated[7] = 0x83;
                unrelated[8] = 0x04;
                byte[] matching = NormalFrame(transactionId, ExpectedData);
                const int prefixLength = 5;
                byte[] firstPart = new byte[unrelated.Length + prefixLength];
                Array.Copy(unrelated, firstPart, unrelated.Length);
                Array.Copy(matching, 0, firstPart, unrelated.Length, prefixLength);
                await stream.WriteAsync(firstPart, 0, firstPart.Length);
                // 전송을 나누지만 TCP Read의 분할 지점 자체를 보장하는 것은 아닙니다.
                await Task.Delay(50);
                await stream.WriteAsync(matching, prefixLength, matching.Length - prefixLength);
            });

            Assert.Null(result.FailureType);
            Assert.Null(result.ExceptionCode);
            Assert.Equal(ExpectedData, result.Data);
        }

        [Fact]
        public async Task ReadHoldingRegistersAsync_OnlyMismatchedFrame_ReturnsTimeout()
        {
            ModbusReadResult result = await ExchangeAsync(async (stream, transactionId) =>
            {
                byte[] unrelated = NormalFrame((ushort)(transactionId + 1), ExpectedData);
                await stream.WriteAsync(unrelated, 0, unrelated.Length);
            });

            Assert.Equal(ModbusFailureType.Timeout, result.FailureType);
            Assert.Null(result.Data);
            Assert.Null(result.ExceptionCode);
        }

        private static async Task<ModbusReadResult> ExchangeAsync(Func<NetworkStream, ushort, Task> sendResponse)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task server = ServeAsync(listener, sendResponse);
                var client = new ModbusTcpClient("127.0.0.1", port);
                ModbusReadResult result = await client.ReadHoldingRegistersAsync(1, 0, 5, TimeSpan.FromSeconds(2));
                if (await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(5))) != server)
                {
                    throw new TimeoutException("테스트용 서버의 종료를 확인하지 못했습니다.");
                }
                await server;
                return result;
            }
            finally
            {
                listener.Stop();
            }
        }

        private static async Task ServeAsync(TcpListener listener, Func<NetworkStream, ushort, Task> sendResponse)
        {
            using (TcpClient connection = await listener.AcceptTcpClientAsync())
            {
                NetworkStream stream = connection.GetStream();
                byte[] request = new byte[12];
                int received = 0;
                while (received < request.Length)
                {
                    int count = await stream.ReadAsync(request, received, request.Length - received);
                    if (count == 0) throw new EndOfStreamException("요청 수신 전에 클라이언트가 종료됐습니다.");
                    received += count;
                }
                ushort transactionId = (ushort)((request[0] << 8) | request[1]);
                await sendResponse(stream, transactionId);
                // 서버가 먼저 닫아서 ConnectionLost가 되지 않도록 클라이언트 종료를 기다립니다.
                byte[] extraRequest = new byte[1];
                try
                {
                    Assert.Equal(0, await stream.ReadAsync(extraRequest, 0, extraRequest.Length));
                }
                catch (IOException)
                {
                    // 시간 초과로 닫힌 클라이언트는 연결 재설정으로도 종료를 알릴 수 있습니다.
                }
            }
        }

        private static byte[] NormalFrame(ushort transactionId, byte[] data)
        {
            byte[] frame = new byte[9 + data.Length];
            frame[0] = (byte)(transactionId >> 8);
            frame[1] = (byte)transactionId;
            int length = frame.Length - 6;
            frame[4] = (byte)(length >> 8);
            frame[5] = (byte)length;
            frame[6] = 1;
            frame[7] = 0x03;
            frame[8] = (byte)data.Length;
            Array.Copy(data, 0, frame, 9, data.Length);
            return frame;
        }
    }
}
