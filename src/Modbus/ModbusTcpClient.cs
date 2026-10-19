using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Core;

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

        public async Task<ModbusReadResult> ReadHoldingRegistersAsync(byte unitId, ushort startAddress, ushort quantity, TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException();
            }
            TcpClient tcpClient = new TcpClient();
            Socket socket = tcpClient.Client;
            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

            using (tcpClient)
            using (cancellationTokenSource)
            {
                CancellationTokenRegistration registration = cancellationTokenSource.Token.Register(tcpClient.Close);
                using (registration)
                {
                    cancellationTokenSource.CancelAfter(timeout);
                    try
                    {
                        await socket.ConnectAsync(_host, _port);
                    }
                    catch (SocketException)
                    {
                        if (cancellationTokenSource.IsCancellationRequested) { return new ModbusReadResult(null, ModbusFailureType.Timeout, null); }
                        return new ModbusReadResult(null, ModbusFailureType.ConnectionFailed, null);
                    }
                    catch (ObjectDisposedException) when (cancellationTokenSource.IsCancellationRequested)
                    {
                        return new ModbusReadResult(null, ModbusFailureType.Timeout, null);

                    }


                    try
                    {
                        NetworkStream networkStream = tcpClient.GetStream();

                        ushort transactionId = 1;
                        byte[] request = ModbusRequestBuilder.BuildReadHoldingRegistersRequest(transactionId, unitId, startAddress, quantity);

                        await networkStream.WriteAsync(request, 0, request.Length);


                        byte[] buffer = new byte[MaxModbusTcpFrameLength];
                        int receivedCount = 0;
                        while (true)
                        {
                            if (cancellationTokenSource.IsCancellationRequested) { return new ModbusReadResult(null, ModbusFailureType.Timeout, null); }

                            // 새로 읽기 전에 이미 받은 데이터에서 완성된 프레임을 처리합니다.
                            if (receivedCount >= 6)
                            {
                                int length = (buffer[4] << 8) | buffer[5];
                                int fullLength = length + 6;
                                if (fullLength < 9 || fullLength > MaxModbusTcpFrameLength)
                                {
                                    return new ModbusReadResult(null, ModbusFailureType.ConnectionLost, null);
                                }

                                byte[] result = new byte[receivedCount];
                                Array.Copy(buffer, result, receivedCount);
                                int? frameLength = ModbusFrameBoundary.GetCompleteFrameLength(result);
                                if (frameLength.HasValue)
                                {
                                    byte[] frame = new byte[frameLength.Value];
                                    Array.Copy(buffer, frame, frame.Length);

                                    if (!ModbusResponseParser.IsTransactionIdMatch(frame, transactionId))
                                    {
                                        System.Diagnostics.Trace.TraceWarning(
                                            "Modbus TCP 응답 폐기: Transaction ID 기대값={0}, 수신값={1}.",
                                            transactionId, (frame[0] << 8) | frame[1]);

                                        // 폐기한 프레임 뒤에 함께 도착한 다음 프레임·부분 프레임을 보존합니다.
                                        int remainingCount = receivedCount - frame.Length;
                                        Array.Copy(buffer, frame.Length, buffer, 0, remainingCount);
                                        receivedCount = remainingCount;
                                        continue;
                                    }

                                    if (frame[7] == 0x03 && frame.Length == 9 + frame[8] && frame[8] == quantity * 2) { }
                                    else if (frame[7] == 0x83 && frame.Length == 9) { }
                                    else
                                    {
                                        return new ModbusReadResult(null, ModbusFailureType.ConnectionLost, null);
                                    }

                                    ModbusReadResponse response = ModbusResponseParser.ParseReadHoldingRegistersResponse(frame);
                                    if (cancellationTokenSource.IsCancellationRequested) { return new ModbusReadResult(null, ModbusFailureType.Timeout, null); }
                                    if (response.ExceptionCode.HasValue)
                                    {
                                        return new ModbusReadResult(null, ModbusFailureType.DeviceException, response.ExceptionCode);
                                    }

                                    return new ModbusReadResult(response.Data, null, null);
                                }
                            }

                            if (buffer.Length - receivedCount == 0) return new ModbusReadResult(null, ModbusFailureType.ConnectionLost, null);
                            int bytesRead = await networkStream.ReadAsync(buffer, receivedCount, buffer.Length - receivedCount);
                            if (bytesRead == 0) throw new System.IO.EndOfStreamException();
                            receivedCount += bytesRead;
                        }
                    }
                    catch (System.IO.IOException)
                    {
                        if (cancellationTokenSource.IsCancellationRequested) { return new ModbusReadResult(null, ModbusFailureType.Timeout, null); }
                        return new ModbusReadResult(null, ModbusFailureType.ConnectionLost, null);
                    }
                    catch (SocketException)
                    {
                        if (cancellationTokenSource.IsCancellationRequested) { return new ModbusReadResult(null, ModbusFailureType.Timeout, null); }
                        return new ModbusReadResult(null, ModbusFailureType.ConnectionLost, null);
                    }
                    catch (ObjectDisposedException) when (cancellationTokenSource.IsCancellationRequested)
                    {
                        return new ModbusReadResult(null, ModbusFailureType.Timeout, null);

                    }
                }

            }

        }
    }
}
