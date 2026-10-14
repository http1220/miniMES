using System;
using System.Globalization;
using System.Net;
using System.Threading;
using FluentModbus;

namespace Simulator
{
    internal sealed class Equipment : IDisposable
    {
        private readonly EquipmentSettings settings;
        private readonly object stateLock = new object();
        private ModbusTcpServer server;
        private ReadOnlyEndpoint endpoint;
        private CancellationTokenSource session;
        private int delay;
        private bool exception;
        private bool high;
        private ushort status = 1;
        private float normalTemperature;
        private float normalVibration;
        private float highTemperature;
        private float highVibration;

        public Equipment(EquipmentSettings settings)
        {
            this.settings = settings;
            normalTemperature = settings.NormalTemperature;
            normalVibration = settings.NormalVibration;
            highTemperature = settings.HighTemperature;
            highVibration = settings.HighVibration;
        }

        // Lifecycle methods are serialized by the command loop. Callbacks only take stateLock.
        public void Start()
        {
            if (server != null) return;
            var nextSession = new CancellationTokenSource();
            var nextServer = new ModbusTcpServer();
            nextServer.RemoveUnit(0);
            nextServer.AddUnit(1);
            nextServer.RequestValidator = (unit, function, address, quantity) =>
                ValidateAndPrepare(nextServer, nextSession.Token, function, address, quantity);
            var nextEndpoint = new ReadOnlyEndpoint(nextServer, nextSession.Token);
            try
            {
                nextEndpoint.Start(new IPEndPoint(settings.Address, settings.Port));
                session = nextSession;
                server = nextServer;
                endpoint = nextEndpoint;
            }
            catch
            {
                nextSession.Cancel();
                nextEndpoint.Dispose();
                nextSession.Dispose();
                throw;
            }
        }

        public void Stop()
        {
            if (server == null) return;
            // Release a validator's delay before FluentModbus waits for its request tasks.
            session.Cancel();
            endpoint.Dispose();
            session.Dispose();
            server = null;
            session = null;
            endpoint = null;
        }

        private ModbusExceptionCode ValidateAndPrepare(ModbusTcpServer target, CancellationToken token,
            ModbusFunctionCode function, ushort address, ushort quantity)
        {
            token.ThrowIfCancellationRequested();
            if (function != ModbusFunctionCode.ReadHoldingRegisters)
                return ModbusExceptionCode.IllegalFunction;
            if (quantity == 0 || quantity > 125)
                return ModbusExceptionCode.IllegalDataValue;
            if (address < settings.StartAddress || (int)address + quantity > settings.StartAddress + 5)
                return ModbusExceptionCode.IllegalDataAddress;

            int requestDelay;
            bool requestException;
            ushort requestStatus;
            float temperature;
            float vibration;
            lock (stateLock)
            {
                requestDelay = delay;
                requestException = exception;
                requestStatus = status;
                temperature = high ? highTemperature : normalTemperature;
                vibration = high ? highVibration : normalVibration;
            }
            if (requestDelay > 0) token.WaitHandle.WaitOne(requestDelay);
            token.ThrowIfCancellationRequested();
            if (requestException) return ModbusExceptionCode.ServerDeviceFailure;

            // FluentModbus holds target.Lock through validation and response serialization.
            var buffer = target.GetHoldingRegisterBuffer(1).Slice(settings.StartAddress * 2, 10);
            WriteFloat(buffer.Slice(0, 4), temperature);
            WriteFloat(buffer.Slice(4, 4), vibration);
            buffer[8] = (byte)(requestStatus >> 8);
            buffer[9] = (byte)requestStatus;
            return ModbusExceptionCode.OK;
        }

        private void WriteFloat(Span<byte> target, float value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            var first = settings.WordOrder == WordOrder.ABCD ? 0 : 2;
            target[0] = bytes[first];
            target[1] = bytes[first + 1];
            target[2] = bytes[2 - first];
            target[3] = bytes[3 - first];
        }

        public void SetDelay(int milliseconds) { lock (stateLock) delay = milliseconds; }
        public void SetException(bool enabled) { lock (stateLock) exception = enabled; }
        public void SetHigh(bool enabled) { lock (stateLock) high = enabled; }
        public void SetStatus(ushort value) { lock (stateLock) status = value; }

        public void SetValues(bool highValues, float temperature, float vibration)
        {
            lock (stateLock)
            {
                if (highValues) { highTemperature = temperature; highVibration = vibration; }
                else { normalTemperature = temperature; normalVibration = vibration; }
            }
        }

        public string Describe()
        {
            lock (stateLock)
                return string.Format(CultureInfo.InvariantCulture,
                    "id={0} address={1} port={2} unit=1 start={3} order={4} delay={5} disconnected={6} exception={7} high={8} status={9} normalTemperature={10:R} normalVibration={11:R} highTemperature={12:R} highVibration={13:R}",
                    settings.Id, settings.Address, settings.Port, settings.StartAddress, settings.WordOrder,
                    delay, server == null, exception, high, status,
                    normalTemperature, normalVibration, highTemperature, highVibration);
        }

        public void Dispose() { Stop(); }
    }
}
