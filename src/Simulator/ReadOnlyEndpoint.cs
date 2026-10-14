using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentModbus;

namespace Simulator
{
    // Own external connections and framing; FluentModbus still validates and serves every FC03.
    // A single private connection avoids the library's concurrent accept/cleanup/Stop list race.
    internal sealed class ReadOnlyEndpoint : IDisposable
    {
        private sealed class Connection
        {
            public TcpClient Client;
            public Task Work;
        }

        private readonly ModbusTcpServer engine;
        private readonly CancellationToken cancellation;
        private readonly SemaphoreSlim exchange = new SemaphoreSlim(1, 1);
        private readonly object connectionsLock = new object();
        private readonly List<Connection> connections = new List<Connection>();
        private TcpListener listener;
        private TcpClient relay;
        private Task accepting;
        private bool disposed;

        public ReadOnlyEndpoint(ModbusTcpServer engine, CancellationToken cancellation)
        {
            this.engine = engine;
            this.cancellation = cancellation;
        }

        public void Start(IPEndPoint address)
        {
            listener = new TcpListener(address);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();

            // Start(TcpClient) uses no FluentModbus accept/cleanup loops. This ephemeral
            // loopback listener exists only while creating the private socket pair.
            var pair = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                pair.Start();
                relay = new TcpClient { NoDelay = true };
                relay.Connect((IPEndPoint)pair.LocalEndpoint);
                var peer = pair.AcceptTcpClient();
                peer.NoDelay = true;
                try { engine.Start(peer); }
                catch { peer.Dispose(); throw; }
            }
            finally { pair.Stop(); }
            accepting = AcceptConnections();
        }

        private async Task AcceptConnections()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    client.NoDelay = true;
                    var connection = new Connection { Client = client };
                    lock (connectionsLock) connections.Add(connection);
                    connection.Work = Serve(connection);
                }
            }
            catch (Exception ex) when (cancellation.IsCancellationRequested &&
                (ex is SocketException || ex is ObjectDisposedException)) { }
        }

        private async Task Serve(Connection connection)
        {
            try
            {
                var stream = connection.Client.GetStream();
                while (!cancellation.IsCancellationRequested)
                {
                    var frame = await ReadFrame(stream).ConfigureAwait(false);
                    if (frame == null) break;
                    if (frame[2] != 0 || frame[3] != 0) break;
                    if (frame[6] != 1) continue;
                    byte[] response;
                    if (frame[7] != 3)
                        response = ExceptionFrame(frame, 1);
                    else if (frame.Length != 12)
                        response = ExceptionFrame(frame, 3);
                    else
                    {
                        await exchange.WaitAsync(cancellation).ConfigureAwait(false);
                        try
                        {
                            var backend = relay.GetStream();
                            await backend.WriteAsync(frame, 0, frame.Length, cancellation).ConfigureAwait(false);
                            response = await ReadFrame(backend).ConfigureAwait(false);
                            if (response == null) throw new IOException("Modbus engine closed its connection.");
                        }
                        finally { exchange.Release(); }
                    }
                    await stream.WriteAsync(response, 0, response.Length, cancellation).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException ||
                ex is ObjectDisposedException || ex is OperationCanceledException)
            {
                // A peer closing a socket, or cancellation during a fault, ends only this connection.
            }
            finally
            {
                connection.Client.Dispose();
                lock (connectionsLock) connections.Remove(connection);
            }
        }

        private async Task<byte[]> ReadFrame(NetworkStream stream)
        {
            var header = new byte[7];
            if (!await ReadExact(stream, header, 0, header.Length).ConfigureAwait(false)) return null;
            var length = (header[4] << 8) | header[5];
            if (length < 2 || length > 254) throw new IOException("Invalid Modbus frame length.");
            var frame = new byte[6 + length];
            Array.Copy(header, frame, header.Length);
            if (!await ReadExact(stream, frame, 7, length - 1).ConfigureAwait(false)) return null;
            return frame;
        }

        private async Task<bool> ReadExact(NetworkStream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var read = await stream.ReadAsync(buffer, offset, count, cancellation).ConfigureAwait(false);
                if (read == 0) return false;
                offset += read;
                count -= read;
            }
            return true;
        }

        private static byte[] ExceptionFrame(byte[] request, byte code)
        {
            return new byte[] { request[0], request[1], 0, 0, 0, 3, 1, (byte)(request[7] | 0x80), code };
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // The owner cancels first, including any validator delay or semaphore wait.
            listener?.Stop();
            accepting?.GetAwaiter().GetResult();
            Connection[] active;
            lock (connectionsLock) active = connections.ToArray();
            foreach (var connection in active) connection.Client.Dispose();
            relay?.Dispose();
            engine.Stop();
            engine.Dispose();
            Task.WhenAll(active.Select(connection => connection.Work)).GetAwaiter().GetResult();
            exchange.Dispose();
        }
    }
}
