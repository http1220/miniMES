using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Collector;
using Core;
using Xunit;

namespace UnitTests
{
    public class CollectorRuntimeTests
    {
        [Fact]
        public async Task RunAsync_Cancellation_CompletesAfterAllLoopsStart()
        {
            using (var test = new CollectorLoopTestContext())
            {
                var readers = new Dictionary<int, FakeModbusReader>();
                Task runtime = test.StartRuntime(device =>
                {
                    var reader = new FakeModbusReader(test.Scheduler);
                    reader.Fallback = (timeout, ct) => Task.FromResult(reader.Success(order: device.WordOrder));
                    readers.Add(device.Id, reader);
                    return reader;
                });

                Assert.Equal(new[] { 1, 2, 3, 4, 5 }, readers.Keys.OrderBy(id => id));
                Assert.All(readers.Values, reader => Assert.Single(reader.Calls));
                await test.Advance(10);
                await test.Stop(runtime);

                Assert.Equal(TaskStatus.RanToCompletion, runtime.Status);
                Assert.Equal(5, test.ReadLogs().Count(row => (string)row["EventKind"] == "StatusSnapshot"));
            }
        }

        [Fact]
        public async Task RunAsync_OneSlowEquipment_IsolatesOtherEquipmentForSixtySeconds()
        {
            using (var test = new CollectorLoopTestContext())
            {
                var responseDelay = TimeSpan.Zero;
                Task runtime = test.StartRuntime(device =>
                {
                    var reader = new FakeModbusReader(test.Scheduler);
                    reader.Fallback = async (timeout, ct) =>
                    {
                        var duration = device.Id == 1 ? responseDelay : TimeSpan.Zero;
                        if (duration > TimeSpan.Zero)
                            await test.Scheduler.Delay(duration > timeout ? timeout : duration, ct);
                        return duration > timeout
                            ? FakeModbusReader.Failure(ModbusFailureType.Timeout)
                            : reader.Success(order: device.WordOrder);
                    };
                    return reader;
                });

                long[] baseline = test.Settings.Equipment.Select(device => test.State.LastSampleSequence(device.Id)).ToArray();
                Assert.All(baseline, sequence => Assert.Equal(1L, sequence));
                responseDelay = TimeSpan.FromSeconds(3);
                await test.Advance(60);
                await test.Stop(runtime);

                Assert.Equal(1L, test.State.LastSampleSequence(1));
                for (int id = 2; id <= 5; id++)
                    Assert.True(test.State.LastSampleSequence(id) - baseline[id - 1] >= 57);

                var raised = Assert.Single(test.State.GetAlarmSnapshot().Where(item =>
                    item.Cause == AlarmCause.Communication && item.EventType == AlarmEventType.Raised));
                Assert.Equal(1, raised.EquipmentId);
            }
        }

        [Fact]
        public async Task RunAsync_ListenerRecovers_StoresSampleWithinFifteenSecondsAndLogsOnce()
        {
            using (var test = new CollectorLoopTestContext())
            {
                var restoredAt = TimeSpan.FromSeconds(3);
                Task runtime = test.StartRuntime(device =>
                {
                    var reader = new FakeModbusReader(test.Scheduler);
                    reader.Fallback = (timeout, ct) => Task.FromResult(device.Id == 1 && test.Scheduler.Now < restoredAt
                        ? FakeModbusReader.Failure(ModbusFailureType.ConnectionFailed)
                        : reader.Success(order: device.WordOrder));
                    return reader;
                });

                await test.Advance(2.999);
                Assert.Equal(0L, test.State.LastSampleSequence(1));
                await test.Advance(15.001);
                await test.Stop(runtime);

                var first = test.State.GetSampleSnapshot().First(item => item.EquipmentId == 1);
                Assert.Equal(1L, first.SequenceNumber);
                Assert.InRange(first.ReceivedAt - restoredAt, TimeSpan.Zero, TimeSpan.FromSeconds(15));
                var recovered = Assert.Single(test.ReadLogs().Where(row => (string)row["EventKind"] == "PollRecovered"));
                Assert.Equal(1, (int)recovered["EquipmentId"]);
                Assert.Equal(2L, (long)recovered["Count"]);
            }
        }

        [Fact]
        public async Task RunAsync_SeveralReceiveDelays_LogsMaximumOnceAndResetsIt()
        {
            using (var test = new CollectorLoopTestContext())
            {
                Task runtime = test.StartRuntime(device =>
                {
                    var reader = new FakeModbusReader(test.Scheduler);
                    reader.Enqueue(async (timeout, ct) =>
                    {
                        var response = reader.Success(order: device.WordOrder);
                        await test.Scheduler.Delay(TimeSpan.FromMilliseconds(30 * device.Id), ct);
                        return response;
                    });
                    return reader;
                });

                await test.Advance(2);
                await test.Stop(runtime);

                var delay = Assert.Single(test.ReadLogs().Where(row => (string)row["EventKind"] == "ReceiveDelay"));
                Assert.Equal(150L, (long)delay["Count"]);
            }
        }
    }
}
