using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Core;

namespace IntegrationTests
{
    internal sealed class FakeModbusDevice : IDisposable
    {
        private byte[] data;
        private int delayMilliseconds;
        private int deviceException;
        private int disconnect;
        private TcpListener listener;
        private CancellationTokenSource stopping;
        private Task server;

        internal int Port { get; private set; }

        internal int DelayMilliseconds
        {
            set => Volatile.Write(ref delayMilliseconds, value);
        }

        internal bool DeviceException
        {
            set => Volatile.Write(ref deviceException, value ? 1 : 0);
        }

        internal bool DisconnectOnRequest
        {
            set => Volatile.Write(ref disconnect, value ? 1 : 0);
        }

        internal FakeModbusDevice(RegisterWordOrder order)
        {
            SetValues(23.456f, 7.89f, 2, order);
            Start();
        }

        internal void SetValues(float temperature, float vibration, ushort status, RegisterWordOrder order)
        {
            var registers = new byte[10];
            WriteFloat(registers, 0, temperature, order);
            WriteFloat(registers, 4, vibration, order);
            registers[8] = (byte)(status >> 8);
            registers[9] = (byte)status;
            Volatile.Write(ref data, registers);
        }

        internal void Start()
        {
            var next = new TcpListener(IPAddress.Loopback, Port);
            next.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            try
            {
                next.Start();
            }
            catch
            {
                next.Stop();
                throw;
            }

            Port = ((IPEndPoint)next.LocalEndpoint).Port;
            listener = next;
            stopping = new CancellationTokenSource();
            var token = stopping.Token;
            server = Task.Run(() => AcceptAsync(next, token));
        }

        internal void Stop()
        {
            if (listener == null)
                return;

            try
            {
                stopping.Cancel();
                listener.Stop();
                server.GetAwaiter().GetResult();
            }
            finally
            {
                stopping.Dispose();
                listener = null;
            }
        }

        public void Dispose() => Stop();

        private async Task AcceptAsync(TcpListener source, CancellationToken ct)
        {
            var requests = new List<Task>();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var client = await source.AcceptTcpClientAsync();
                    requests.RemoveAll(task => task.Status == TaskStatus.RanToCompletion);
                    requests.Add(RespondAsync(client, ct));
                }
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
            }
            catch (InvalidOperationException) when (ct.IsCancellationRequested)
            {
            }
            finally
            {
                await Task.WhenAll(requests);
            }
        }

        private async Task RespondAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            using (ct.Register(client.Close))
            {
                try
                {
                    var stream = client.GetStream();
                    var request = new byte[12];
                    int offset = 0;
                    while (offset < request.Length)
                    {
                        int count = await stream.ReadAsync(request, offset, request.Length - offset, ct);
                        if (count == 0)
                            return;
                        offset += count;
                    }

                    if (Volatile.Read(ref disconnect) != 0)
                        return;

                    byte[] registers = Volatile.Read(ref data);
                    bool exception = Volatile.Read(ref deviceException) != 0;
                    await Task.Delay(Volatile.Read(ref delayMilliseconds), ct);
                    var frame = new byte[exception ? 9 : 9 + registers.Length];
                    frame[0] = request[0];
                    frame[1] = request[1];
                    frame[5] = (byte)(frame.Length - 6);
                    frame[6] = request[6];
                    frame[7] = exception ? (byte)0x83 : (byte)0x03;
                    frame[8] = exception ? (byte)0x04 : (byte)registers.Length;
                    if (!exception)
                        Array.Copy(registers, 0, frame, 9, registers.Length);
                    await stream.WriteAsync(frame, 0, frame.Length, ct);
                }
                catch (IOException)
                {
                    // A timed-out reader closes its socket.
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                }
                catch (ObjectDisposedException) when (ct.IsCancellationRequested)
                {
                }
                catch (InvalidOperationException) when (ct.IsCancellationRequested)
                {
                }
            }
        }

        private static void WriteFloat(byte[] registers, int offset, float value, RegisterWordOrder order)
        {
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            int first = order == RegisterWordOrder.ABCD ? 0 : 2;
            Array.Copy(bytes, first, registers, offset, 2);
            Array.Copy(bytes, 2 - first, registers, offset + 2, 2);
        }
    }
}
