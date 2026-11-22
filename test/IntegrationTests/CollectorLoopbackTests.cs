using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Collector;
using Core;
using Xunit;

namespace IntegrationTests
{
    public class CollectorLoopbackTests
    {
        [Fact]
        [Trait("Category", "Slow")]
        public Task RunAsync_OneDeviceDelayed_OtherDevicesContinueSampling()
        {
            return WithCollectorAsync(async (devices, state, time) =>
            {
                await WaitUntilAsync(() => Enumerable.Range(1, 5)
                    .All(id => state.LastSampleSequence(id) >= 2), time, TimeSpan.FromSeconds(10));

                long[] baseline = Enumerable.Range(1, 5).Select(state.LastSampleSequence).ToArray();
                long lastEvent = state.GetAlarmSnapshot().Select(item => item.EventSequence).DefaultIfEmpty().Max();
                devices[0].DelayMilliseconds = 3000;
                var end = time.Now() + TimeSpan.FromSeconds(60);
                var remaining = end - time.Now();
                while (remaining > TimeSpan.Zero)
                {
                    await time.Delay(remaining, CancellationToken.None);
                    remaining = end - time.Now();
                }

                for (int id = 2; id <= 5; id++)
                {
                    long count = state.LastSampleSequence(id) - baseline[id - 1];
                    Assert.True(count >= 57, $"Equipment {id} collected {count} samples.");
                }

                var communication = state.GetAlarmSnapshot().Where(item => item.EventSequence > lastEvent &&
                    item.Cause == AlarmCause.Communication && item.EventType == AlarmEventType.Raised).ToArray();
                Assert.NotEmpty(communication);
                Assert.All(communication, item => Assert.Equal(1, item.EquipmentId));
            });
        }

        [Fact]
        public Task RunAsync_ListenerRestarts_CollectsSampleWithinFifteenSeconds()
        {
            return WithCollectorAsync(async (devices, state, time) =>
            {
                await WaitUntilAsync(() => state.LastSampleSequence(1) > 0, time, TimeSpan.FromSeconds(10));
                devices[0].Stop();
                await time.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);

                long baseline = state.LastSampleSequence(1);
                var restart = time.Now();
                devices[0].Start();
                await WaitUntilAsync(() => state.LastSampleSequence(1) > baseline, time, TimeSpan.FromSeconds(15));

                var first = state.GetSampleSnapshot().First(item => item.EquipmentId == 1 &&
                    item.SequenceNumber > baseline);
                Assert.InRange(first.ReceivedAt - restart, TimeSpan.Zero, TimeSpan.FromSeconds(15));
            });
        }

        [Fact]
        public Task RunAsync_ValidFrames_StoreValuesInBothWordOrders()
        {
            return WithCollectorAsync(async (devices, state, time) =>
            {
                await WaitUntilAsync(() => state.LastSampleSequence(1) > 0 && state.LastSampleSequence(2) > 0,
                    time, TimeSpan.FromSeconds(10));

                var samples = state.GetSampleSnapshot();
                foreach (int id in new[] { 1, 2 })
                {
                    var sample = samples.First(item => item.EquipmentId == id);
                    Assert.Equal((float?)23.456f, sample.Temperature);
                    Assert.Equal((float?)7.89f, sample.Vibration);
                    Assert.Equal((byte?)2, sample.StatusCode);
                }
            });
        }

        private static async Task WithCollectorAsync(
            Func<FakeModbusDevice[], CollectorState, CollectorTime, Task> scenario)
        {
            var devices = new FakeModbusDevice[5];
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            try
            {
                var values = new Dictionary<string, string>
                {
                    ["SERVER_BASE_URL"] = "https://change-me:5443",
                    ["COLLECTOR_API_KEY"] = "change-me-change-me-change-me-0001"
                };
                for (int i = 0; i < devices.Length; i++)
                {
                    devices[i] = new FakeModbusDevice(i % 2 == 0 ? RegisterWordOrder.ABCD : RegisterWordOrder.CDAB);
                    values[$"Equipment.{i + 1}.Port"] = devices[i].Port.ToString(CultureInfo.InvariantCulture);
                }

                Assert.Empty(CollectorSettings.Validate(values, out var settings));
                var state = new CollectorState(Guid.NewGuid(), settings.Equipment.Select(device => device.Id),
                    settings.SampleBufferCapacity, settings.AlarmQueueCapacity,
                    settings.CommunicationRecoverySuccessCount, settings.ThresholdConsecutiveCount);
                var time = CollectorTime.StartReal();
                using (var log = new CollectorLog(directory, state.RunId))
                using (var cancellation = new CancellationTokenSource())
                {
                    var runtime = new CollectorRuntime(settings, state, log,
                        device => new ModbusTcpClientReader(device.Host, device.Port, time.Now),
                        time.Now, time.Delay, id => () => 1);
                    Task running = Task.Run(() => runtime.RunAsync(cancellation.Token));
                    try
                    {
                        await scenario(devices, state, time);
                    }
                    finally
                    {
                        cancellation.Cancel();
                        await running;
                    }
                }
            }
            finally
            {
                foreach (var device in devices)
                    device?.Dispose();
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, CollectorTime time, TimeSpan timeout)
        {
            var end = time.Now() + timeout;
            while (!predicate() && time.Now() < end)
                await time.Delay(TimeSpan.FromMilliseconds(25), CancellationToken.None);
            Assert.True(predicate(), "The collector did not advance before the deadline.");
        }
    }
}
