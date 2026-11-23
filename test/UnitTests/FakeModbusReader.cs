using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Collector;
using Core;
using Modbus;
using Newtonsoft.Json.Linq;
using Xunit;

namespace UnitTests
{
    internal sealed class FakeModbusReader : IModbusReader
    {
        private readonly VirtualScheduler scheduler;
        private readonly Queue<Func<TimeSpan, CancellationToken, Task<ModbusReadResult>>> script =
            new Queue<Func<TimeSpan, CancellationToken, Task<ModbusReadResult>>>();

        internal List<(TimeSpan At, byte UnitId, ushort Address, ushort Quantity, TimeSpan Timeout)> Calls { get; } =
            new List<(TimeSpan, byte, ushort, ushort, TimeSpan)>();

        internal Func<TimeSpan, CancellationToken, Task<ModbusReadResult>> Fallback { get; set; }

        internal FakeModbusReader(VirtualScheduler scheduler)
        {
            this.scheduler = scheduler;
            Fallback = (timeout, ct) => Task.FromResult(Success());
        }

        internal void Enqueue(Func<TimeSpan, CancellationToken, Task<ModbusReadResult>> read) => script.Enqueue(read);

        internal void Enqueue(params Func<ModbusReadResult>[] reads)
        {
            foreach (var read in reads)
                Enqueue((timeout, ct) => Task.FromResult(read()));
        }

        public Task<ModbusReadResult> ReadHoldingRegistersAsync(byte unitId, ushort startAddress,
            ushort quantity, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add((scheduler.Now, unitId, startAddress, quantity, timeout));
            var read = script.Count > 0 ? script.Dequeue() : Fallback;
            return read(timeout, cancellationToken);
        }

        internal ModbusReadResult Success(float temperature = 25, float vibration = 1, ushort status = 1,
            RegisterWordOrder order = RegisterWordOrder.ABCD)
        {
            var data = new byte[10];
            WriteFloat(data, 0, temperature, order);
            WriteFloat(data, 4, vibration, order);
            data[8] = (byte)(status >> 8);
            data[9] = (byte)status;
            return new ModbusReadResult(data, null, null, scheduler.Now);
        }

        internal static ModbusReadResult Failure(ModbusFailureType kind)
        {
            return new ModbusReadResult(null, kind, kind == ModbusFailureType.DeviceException ? (byte?)4 : null);
        }

        private static void WriteFloat(byte[] data, int offset, float value, RegisterWordOrder order)
        {
            uint bits = BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);
            int high = order == RegisterWordOrder.ABCD ? offset : offset + 2;
            int low = order == RegisterWordOrder.ABCD ? offset + 2 : offset;
            data[high] = (byte)(bits >> 24);
            data[high + 1] = (byte)(bits >> 16);
            data[low] = (byte)(bits >> 8);
            data[low + 1] = (byte)bits;
        }
    }

    internal sealed class CollectorLoopTestContext : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        private bool logClosed;

        internal VirtualScheduler Scheduler { get; } = new VirtualScheduler();
        internal CollectorSettings Settings { get; }
        internal CollectorState State { get; }
        internal CollectorLog Log { get; }

        internal CollectorLoopTestContext(Dictionary<string, string> overrides = null)
        {
            var values = new Dictionary<string, string>
            {
                ["SERVER_BASE_URL"] = "https://change-me:5443",
                ["COLLECTOR_API_KEY"] = "change-me-change-me-change-me-0001"
            };
            if (overrides != null)
            {
                foreach (var entry in overrides)
                    values[entry.Key] = entry.Value;
            }
            Assert.Empty(CollectorSettings.Validate(values, out var settings));
            Settings = settings;
            State = new CollectorState(Guid.NewGuid(), Settings.Equipment.Select(device => device.Id),
                Settings.SampleBufferCapacity, Settings.AlarmQueueCapacity,
                Settings.CommunicationRecoverySuccessCount, Settings.ThresholdConsecutiveCount);
            Log = new CollectorLog(directory, State.RunId);
        }

        internal Task StartEquipment(FakeModbusReader reader, int equipmentId = 1, double random = 0)
        {
            var loop = new EquipmentLoop(Settings.Equipment[equipmentId - 1], Settings, State, reader,
                Scheduler.NowProvider, Scheduler.DelayProvider, Log, () => random);
            return Scheduler.RunAsync(() => loop.RunAsync(cancellation.Token));
        }

        internal Task StartJudge()
        {
            var loop = new JudgeLoop(Settings, State, Scheduler.NowProvider, Scheduler.DelayProvider, Log);
            return Scheduler.RunAsync(() => loop.RunAsync(cancellation.Token));
        }

        internal Task StartRuntime(Func<EquipmentSettings, IModbusReader> factory)
        {
            var runtime = new CollectorRuntime(Settings, State, Log, factory,
                Scheduler.NowProvider, Scheduler.DelayProvider, id => () => 0);
            return Scheduler.RunAsync(() => runtime.RunAsync(cancellation.Token));
        }

        internal Task Advance(double seconds) => Scheduler.AdvanceAsync(TimeSpan.FromSeconds(seconds));

        internal async Task Stop(params Task[] loops)
        {
            cancellation.Cancel();
            await Advance(0);
            await Task.WhenAll(loops);
        }

        internal JObject[] ReadLogs()
        {
            Log.Dispose();
            logClosed = true;
            if (!Directory.Exists(directory))
                return Array.Empty<JObject>();
            return Directory.EnumerateFiles(directory, "collector-*.log")
                .OrderBy(path => path, StringComparer.Ordinal)
                .SelectMany(File.ReadLines).Select(JObject.Parse).ToArray();
        }

        public void Dispose()
        {
            cancellation.Cancel();
            cancellation.Dispose();
            if (!logClosed)
                Log.Dispose();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
