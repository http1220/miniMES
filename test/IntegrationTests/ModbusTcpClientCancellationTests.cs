using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Core;
using Modbus;
using Xunit;

namespace IntegrationTests
{
    public class ModbusTcpClientCancellationTests
    {
        private static readonly byte[] ExpectedData = { 0x12, 0x34 };

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ReadHoldingRegistersAsync_ValidFrame_UsesClockOnce(bool sendMismatchedFrame)
        {
            int calls = 0;
            TimeSpan receivedAt = TimeSpan.FromSeconds(42);
            ModbusReadResult result = await ExchangeAsync(async (stream, transactionId) =>
            {
                if (sendMismatchedFrame)
                {
                    byte[] unrelated = NormalFrame((ushort)(transactionId + 1), new byte[] { 0xDE, 0xAD });
                    await stream.WriteAsync(unrelated, 0, unrelated.Length);
                }

                await SendNormalFrameAsync(stream, transactionId);
            }, () =>
            {
                calls++;
                return receivedAt;
            });

            Assert.Null(result.FailureType);
            Assert.Equal(ExpectedData, result.Data);
            Assert.Equal((TimeSpan?)receivedAt, result.ReceivedAt);
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task ReadHoldingRegistersAsync_ValidFrameWithLegacyConstructor_ReturnsNoReceivedAt()
        {
            ModbusReadResult result = await ExchangeAsync(SendNormalFrameAsync, clock: null);

            Assert.Null(result.FailureType);
            Assert.Equal(ExpectedData, result.Data);
            Assert.Null(result.ReceivedAt);
        }

        [Theory]
        [InlineData(ModbusFailureType.DeviceException)]
        [InlineData(ModbusFailureType.Timeout)]
        [InlineData(ModbusFailureType.ConnectionLost)]
        public async Task ReadHoldingRegistersAsync_FailedResponse_DoesNotUseClock(ModbusFailureType failureType)
        {
            int calls = 0;
            ModbusReadResult result = await ExchangeAsync(async (stream, transactionId) =>
            {
                if (failureType == ModbusFailureType.DeviceException)
                {
                    byte[] frame = NormalFrame(transactionId, new byte[0]);
                    frame[7] = 0x83;
                    frame[8] = 0x04;
                    await stream.WriteAsync(frame, 0, frame.Length);
                }
            }, () =>
            {
                calls++;
                return TimeSpan.FromSeconds(42);
            }, closeAfterResponse: failureType == ModbusFailureType.ConnectionLost);

            Assert.Equal(failureType, result.FailureType);
            Assert.Equal(failureType == ModbusFailureType.DeviceException ? (byte?)0x04 : null, result.ExceptionCode);
            Assert.Null(result.Data);
            Assert.Null(result.ReceivedAt);
            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task ReadHoldingRegistersAsync_CanceledWhileAwaitingResponse_ThrowsBeforeTimeout()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    var requestReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Task server = ServeAsync(listener, (stream, transactionId) =>
                    {
                        requestReceived.SetResult(true);
                        return Task.CompletedTask;
                    }, closeAfterResponse: false);
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    var client = new ModbusTcpClient("127.0.0.1", port);
                    Task<ModbusReadResult> read = client.ReadHoldingRegistersAsync(
                        1, 0, 1, TimeSpan.FromSeconds(10), cancellation.Token);

                    await WaitForCompletionAsync(requestReceived.Task, TimeSpan.FromSeconds(5));
                    cancellation.Cancel();

                    OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        () => WaitForCompletionAsync(read, TimeSpan.FromSeconds(2)));
                    Assert.Equal(cancellation.Token, exception.CancellationToken);
                    await WaitForCompletionAsync(server, TimeSpan.FromSeconds(5));
                }
                finally
                {
                    cancellation.Cancel();
                    listener.Stop();
                }
            }
        }

        [Fact]
        public async Task ReadHoldingRegistersAsync_AlreadyCanceled_ThrowsWithoutConnecting()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    var client = new ModbusTcpClient("127.0.0.1", port);
                    cancellation.Cancel();

                    OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        () => client.ReadHoldingRegistersAsync(1, 0, 1, TimeSpan.FromSeconds(2), cancellation.Token));
                    Assert.Equal(cancellation.Token, exception.CancellationToken);
                    Assert.False(listener.Pending());
                }
                finally
                {
                    listener.Stop();
                }
            }
        }

        [Fact]
        public async Task ReadHoldingRegistersAsync_ConnectionRefused_ReturnsConnectionFailed()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            var client = new ModbusTcpClient("127.0.0.1", port);
            ModbusReadResult result = await client.ReadHoldingRegistersAsync(1, 0, 1, TimeSpan.FromSeconds(2));

            Assert.Equal(ModbusFailureType.ConnectionFailed, result.FailureType);
            Assert.Null(result.ReceivedAt);
        }

        private static async Task<ModbusReadResult> ExchangeAsync(
            Func<NetworkStream, ushort, Task> sendResponse, Func<TimeSpan> clock, bool closeAfterResponse = false)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task server = ServeAsync(listener, sendResponse, closeAfterResponse);
                var client = clock == null
                    ? new ModbusTcpClient("127.0.0.1", port)
                    : new ModbusTcpClient("127.0.0.1", port, clock);
                ModbusReadResult result = await client.ReadHoldingRegistersAsync(1, 0, 1, TimeSpan.FromSeconds(2));
                await WaitForCompletionAsync(server, TimeSpan.FromSeconds(5));
                return result;
            }
            finally
            {
                listener.Stop();
            }
        }

        private static async Task ServeAsync(
            TcpListener listener, Func<NetworkStream, ushort, Task> sendResponse, bool closeAfterResponse)
        {
            using (TcpClient connection = await listener.AcceptTcpClientAsync())
            {
                NetworkStream stream = connection.GetStream();
                byte[] request = new byte[12];
                int received = 0;
                while (received < request.Length)
                {
                    int count = await stream.ReadAsync(request, received, request.Length - received);
                    if (count == 0) throw new EndOfStreamException("The client closed before sending its request.");
                    received += count;
                }

                ushort transactionId = (ushort)((request[0] << 8) | request[1]);
                await sendResponse(stream, transactionId);
                if (closeAfterResponse)
                {
                    return;
                }

                byte[] extraRequest = new byte[1];
                try
                {
                    Assert.Equal(0, await stream.ReadAsync(extraRequest, 0, extraRequest.Length));
                }
                catch (IOException)
                {
                    // A client closing during a read can reset the connection.
                }
            }
        }

        private static async Task WaitForCompletionAsync(Task task, TimeSpan timeout)
        {
            if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
            {
                throw new TimeoutException("The loopback operation did not complete in time.");
            }

            await task;
        }

        private static Task SendNormalFrameAsync(NetworkStream stream, ushort transactionId)
        {
            byte[] frame = NormalFrame(transactionId, ExpectedData);
            return stream.WriteAsync(frame, 0, frame.Length);
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
