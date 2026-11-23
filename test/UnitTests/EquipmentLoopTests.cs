using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Collector;
using Core;
using Modbus;
using Xunit;

namespace UnitTests
{
    public class EquipmentLoopTests
    {
        [Theory]
        [InlineData(1, RegisterWordOrder.ABCD)]
        [InlineData(2, RegisterWordOrder.CDAB)]
        public async Task RunAsync_ValidResponses_StoreDecodedSamplesInOrder(int equipmentId, RegisterWordOrder order)
        {
            using (var test = new CollectorLoopTestContext())
            {
                var reader = new FakeModbusReader(test.Scheduler);
                reader.Fallback = (timeout, ct) => Task.FromResult(reader.Success(23.456f, 7.89f, 2, order));
                Task loop = test.StartEquipment(reader, equipmentId);

                await test.Advance(2);
                await test.Stop(loop);

                var samples = test.State.GetSampleSnapshot();
                Assert.Equal(new long[] { 1, 2, 3 }, samples.Select(item => item.SequenceNumber));
                Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) },
                    samples.Select(item => item.ReceivedAt));
                Assert.All(samples, item =>
                {
                    Assert.Equal(equipmentId, item.EquipmentId);
                    Assert.Equal((float?)23.456f, item.Temperature);
                    Assert.Equal((float?)7.89f, item.Vibration);
                    Assert.Equal((byte?)2, item.StatusCode);
                });
                var call = reader.Calls[0];
                Assert.Equal(test.Settings.Equipment[equipmentId - 1].UnitId, call.UnitId);
                Assert.Equal(test.Settings.Equipment[equipmentId - 1].StartAddress, call.Address);
                Assert.Equal((ushort)5, call.Quantity);
                Assert.Equal(test.Settings.ReadTimeout, call.Timeout);
            }
        }

        [Fact]
        public async Task RunAsync_InvalidValues_StoresSampleWithNullFields()
        {
            using (var test = new CollectorLoopTestContext())
            {
                var reader = new FakeModbusReader(test.Scheduler);
                reader.Enqueue(() => reader.Success(201, float.NaN, 256),
                    () => reader.Success(float.PositiveInfinity, 1, 0));
                Task loop = test.StartEquipment(reader);

                await test.Advance(1);
                await test.Stop(loop);

                var samples = test.State.GetSampleSnapshot();
                Assert.Equal(2, samples.Length);
                Assert.Null(samples[0].Temperature);
                Assert.Null(samples[0].Vibration);
                Assert.Null(samples[0].StatusCode);
                Assert.Null(samples[1].Temperature);
                Assert.Equal((float?)1, samples[1].Vibration);
                Assert.Equal((byte?)0, samples[1].StatusCode);
            }
        }

        [Theory]
        [InlineData(ModbusFailureType.Timeout)]
        [InlineData(ModbusFailureType.DeviceException)]
        [InlineData(ModbusFailureType.InvalidValue)]
        public async Task RunAsync_NonReconnectFailure_WaitsForNextPoll(ModbusFailureType kind)
        {
            using (var test = new CollectorLoopTestContext(new Dictionary<string, string>
            {
                ["PollIntervalMilliseconds"] = "200",
                ["ReadTimeoutMilliseconds"] = "100"
            }))
            {
                var reader = new FakeModbusReader(test.Scheduler);
                reader.Fallback = (timeout, ct) => Task.FromResult(FakeModbusReader.Failure(kind));
                Task loop = test.StartEquipment(reader);

                await test.Advance(0.199);
                Assert.Single(reader.Calls);
                await test.Advance(0.001);
                await test.Stop(loop);

                Assert.Equal(new[] { TimeSpan.Zero, test.Settings.PollInterval }, reader.Calls.Select(call => call.At));
                Assert.Empty(test.State.GetSampleSnapshot());
                var failures = test.ReadLogs().Where(row => (string)row["EventKind"] == "PollFailed").ToArray();
                Assert.Equal(2, failures.Length);
                Assert.All(failures, row => Assert.Equal(kind.ToString(), (string)row["FailureType"]));
            }
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(1.0)]
        public async Task RunAsync_ConsecutiveReconnectFailures_UsesCappedBackoffWithJitter(double random)
        {
            using (var test = new CollectorLoopTestContext())
            {
                var reader = new FakeModbusReader(test.Scheduler);
                for (int i = 0; i < 7; i++)
                {
                    var kind = i % 2 == 0 ? ModbusFailureType.ConnectionFailed : ModbusFailureType.ConnectionLost;
                    reader.Enqueue(() => FakeModbusReader.Failure(kind));
                }
                Task loop = test.StartEquipment(reader, random: random);

                double[] expected = random == 0 ? new double[] { 1, 2, 4, 8, 10, 10 }
                    : new[] { 1.2, 2.4, 4.8, 9.6, 12, 12 };
                await test.Advance(expected.Sum());
                await test.Stop(loop);

                Assert.Equal(7, reader.Calls.Count);
                Assert.Empty(test.State.GetSampleSnapshot());
                for (int i = 0; i < expected.Length; i++)
                    Assert.Equal(TimeSpan.FromSeconds(expected[i]), reader.Calls[i + 1].At - reader.Calls[i].At);
            }
        }

        [Fact]
        public async Task RunAsync_ValidResponseAfterFailures_ResetsConfiguredBackoff()
        {
            using (var test = new CollectorLoopTestContext(new Dictionary<string, string>
            {
                ["BackoffMaximumMilliseconds"] = "2500",
                ["BackoffMaximumJitterFraction"] = "0.1"
            }))
            {
                var reader = new FakeModbusReader(test.Scheduler);
                Func<ModbusReadResult> lost = () => FakeModbusReader.Failure(ModbusFailureType.ConnectionLost);
                reader.Enqueue(lost, () => FakeModbusReader.Failure(ModbusFailureType.Timeout),
                    lost, lost, () => reader.Success(), lost);
                Task loop = test.StartEquipment(reader, random: 1);

                await test.Advance(9.15);
                await test.Stop(loop);

                Assert.Equal(new[] { 0d, 1.1, 2.1, 4.3, 7.05, 8.05, 9.15 },
                    reader.Calls.Select(call => Math.Round(call.At.TotalSeconds, 3)));
                var recovery = test.ReadLogs().Where(row => (string)row["EventKind"] == "PollRecovered").ToArray();
                Assert.Equal(new long[] { 4, 1 }, recovery.Select(row => (long)row["Count"]));
            }
        }

        [Fact]
        public async Task RunAsync_FailureDuringCommunicationRecovery_RequiresThreeNewSuccesses()
        {
            using (var test = new CollectorLoopTestContext())
            {
                test.State.OnValidResponse(1, -test.Settings.CommunicationFault);
                Task judge = test.StartJudge();
                await test.Advance(1);
                var raised = Assert.Single(test.State.GetAlarmSnapshot().Where(item => item.Cause == AlarmCause.Communication));

                var reader = new FakeModbusReader(test.Scheduler);
                reader.Enqueue(() => reader.Success(), () => reader.Success(),
                    () => FakeModbusReader.Failure(ModbusFailureType.Timeout));
                Task loop = test.StartEquipment(reader);
                await test.Advance(4);
                Assert.Single(test.State.GetAlarmSnapshot().Where(item => item.Cause == AlarmCause.Communication));
                await test.Advance(1);
                await test.Stop(loop, judge);

                var communication = test.State.GetAlarmSnapshot()
                    .Where(item => item.Cause == AlarmCause.Communication).ToArray();
                Assert.Equal(new[] { AlarmEventType.ReportAbnormal, AlarmEventType.Cleared },
                    communication.Select(item => item.EventType));
                Assert.Same(raised, communication[0]);
                var cleared = communication[1];
                Assert.True(cleared.EventSequence > raised.EventSequence);
                Assert.Equal(TimeSpan.FromSeconds(6), cleared.OccurredAt);
                var clearLog = Assert.Single(test.ReadLogs().Where(row => (string)row["EventKind"] == "AlarmCleared"));
                Assert.Equal("Communication", (string)clearLog["Detail"]);
                Assert.Equal(cleared.EventSequence, (long)clearLog["Count"]);
            }
        }

        [Fact]
        public async Task RunAsync_ThresholdTransitions_InvalidSamplesDoNotChangeCounters()
        {
            using (var test = new CollectorLoopTestContext(new Dictionary<string, string>
            {
                ["ThresholdConsecutiveCount"] = "3"
            }))
            {
                var reader = new FakeModbusReader(test.Scheduler);
                reader.Enqueue(() => reader.Success(90), () => reader.Success(201),
                    () => reader.Success(90), () => reader.Success(90),
                    () => reader.Success(80), () => reader.Success(201),
                    () => reader.Success(80), () => reader.Success(80));
                AlarmEventItem[] TemperatureEvents() => test.State.GetAlarmSnapshot()
                    .Where(item => item.Cause == AlarmCause.Temperature).ToArray();

                Task loop = test.StartEquipment(reader);
                await test.Advance(2);
                Assert.Empty(TemperatureEvents());
                await test.Advance(1);
                Assert.Equal(AlarmEventType.ReportAbnormal, Assert.Single(TemperatureEvents()).EventType);
                await test.Advance(3);
                Assert.Single(TemperatureEvents());
                await test.Advance(1);
                await test.Stop(loop);

                var events = TemperatureEvents();
                Assert.Equal(new[] { AlarmEventType.ReportAbnormal, AlarmEventType.Cleared },
                    events.Select(item => item.EventType));
            }
        }

        [Fact]
        public async Task RunAsync_FirstNormalTemperatureReport_SkipsNullMeasurements()
        {
            using (var test = new CollectorLoopTestContext(new Dictionary<string, string>
            {
                ["ThresholdConsecutiveCount"] = "3"
            }))
            {
                var reader = new FakeModbusReader(test.Scheduler);
                reader.Enqueue(() => reader.Success(80), () => reader.Success(201),
                    () => reader.Success(80), () => reader.Success(80));
                Task loop = test.StartEquipment(reader);

                await test.Advance(2);
                Assert.Null(test.State.GetSampleSnapshot()[1].Temperature);
                Assert.DoesNotContain(test.State.GetAlarmSnapshot(), item => item.Cause == AlarmCause.Temperature);
                await test.Advance(1);
                var report = Assert.Single(test.State.GetAlarmSnapshot()
                    .Where(item => item.Cause == AlarmCause.Temperature));
                Assert.Equal(AlarmEventType.ReportNormal, report.EventType);
                Assert.Equal(TimeSpan.FromSeconds(3), report.OccurredAt);
                await test.Advance(1);
                await test.Stop(loop);

                Assert.Same(report, Assert.Single(test.State.GetAlarmSnapshot()
                    .Where(item => item.Cause == AlarmCause.Temperature)));
                var row = Assert.Single(test.ReadLogs().Where(entry => (string)entry["EventKind"] == "StatusReported" &&
                    (string)entry["Detail"] == "Temperature"));
                Assert.Equal(report.EventSequence, (long)row["Count"]);
            }
        }

        [Fact]
        public async Task RunAsync_CanceledDuringRead_ReturnsNormally()
        {
            using (var test = new CollectorLoopTestContext())
            {
                var reader = new FakeModbusReader(test.Scheduler);
                reader.Enqueue(async (timeout, ct) =>
                {
                    await test.Scheduler.Delay(timeout, ct);
                    return reader.Success();
                });
                Task loop = test.StartEquipment(reader);
                await test.Stop(loop);

                Assert.Equal(TaskStatus.RanToCompletion, loop.Status);
                Assert.Empty(test.State.GetSampleSnapshot());
            }
        }

        [Fact]
        public async Task RunAsync_ResponseProcessedLater_PreservesReceiveTime()
        {
            using (var test = new CollectorLoopTestContext())
            {
                var reader = new FakeModbusReader(test.Scheduler);
                reader.Enqueue(async (timeout, ct) =>
                {
                    await test.Scheduler.Delay(TimeSpan.FromMilliseconds(100), ct);
                    var result = reader.Success();
                    await test.Scheduler.Delay(TimeSpan.FromMilliseconds(200), ct);
                    return result;
                });
                Task loop = test.StartEquipment(reader);
                await test.Advance(0.299);
                Assert.Empty(test.State.GetSampleSnapshot());
                await test.Advance(0.001);
                await test.Stop(loop);

                Assert.Equal(TimeSpan.FromMilliseconds(100), Assert.Single(test.State.GetSampleSnapshot()).ReceivedAt);
            }
        }

        [Fact]
        public async Task RunAsync_FailureAtPollDeadline_YieldsThroughZeroDelay()
        {
            using (var test = new CollectorLoopTestContext())
            {
                var reader = new FakeModbusReader(test.Scheduler);
                reader.Enqueue(async (timeout, ct) =>
                {
                    await test.Scheduler.Delay(test.Settings.PollInterval, ct);
                    return FakeModbusReader.Failure(ModbusFailureType.Timeout);
                });
                Task loop = test.StartEquipment(reader);
                await test.Advance(1);
                Assert.Single(reader.Calls);
                await test.Advance(0);
                Assert.Equal(2, reader.Calls.Count);
                await test.Advance(0);
                Assert.Equal(2, reader.Calls.Count);
                await test.Stop(loop);
            }
        }
    }
}
