using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Collector;
using Core;
using Xunit;

namespace UnitTests
{
    public class JudgeLoopTests
    {
        [Fact]
        public async Task RunAsync_CommunicationTimeout_RaisesOnceForEquipmentWithHistory()
        {
            using (var test = new CollectorLoopTestContext(new Dictionary<string, string>
            {
                ["CommunicationFaultMilliseconds"] = "3000"
            }))
            {
                test.State.OnValidResponse(1, TimeSpan.Zero);
                Task loop = test.StartJudge();
                Assert.Empty(test.State.GetAlarmSnapshot());

                await test.Advance(2.999);
                Assert.Empty(test.State.GetAlarmSnapshot());
                await test.Advance(0.001);
                var raised = Assert.Single(test.State.GetAlarmSnapshot());
                Assert.Equal(1, raised.EquipmentId);
                Assert.Equal(AlarmCause.Communication, raised.Cause);
                Assert.Equal(AlarmEventType.Raised, raised.EventType);
                Assert.Equal(TimeSpan.FromSeconds(3), raised.OccurredAt);
                await test.Advance(2);
                await test.Stop(loop);

                Assert.Same(raised, Assert.Single(test.State.GetAlarmSnapshot()));
                var row = Assert.Single(test.ReadLogs().Where(entry => (string)entry["EventKind"] == "AlarmRaised"));
                Assert.Equal("Communication", (string)row["Detail"]);
                Assert.Equal(raised.EventSequence, (long)row["Count"]);
            }
        }

        [Fact]
        public async Task RunAsync_BufferPressureAndFreedSlot_LogsCounterChangesAndRequeuesOriginal()
        {
            using (var test = new CollectorLoopTestContext(new Dictionary<string, string>
            {
                ["SampleBufferCapacity"] = "1",
                ["AlarmQueueCapacity"] = "1"
            }))
            {
                Task loop = test.StartJudge();
                test.State.AddSample(1, TimeSpan.Zero, 25, 1, 1);
                test.State.AddSample(1, TimeSpan.Zero, 26, 1, 1);
                var queued = test.State.SubmitAlarm(2, AlarmCause.Temperature, AlarmEvent.Raised, TimeSpan.Zero);
                test.State.OnValidResponse(1, TimeSpan.Zero);

                await test.Advance(5);
                Assert.Equal(1, test.State.UnsentAlarmCount);
                Assert.Same(queued.Item, Assert.Single(test.State.GetAlarmSnapshot()));
                test.State.RemoveAlarmEvents(new HashSet<long> { queued.Item.EventSequence });

                await test.Advance(1);
                var requeued = Assert.Single(test.State.GetAlarmSnapshot());
                Assert.Equal(queued.Item.EventSequence + 1, requeued.EventSequence);
                Assert.Equal(TimeSpan.FromSeconds(5), requeued.OccurredAt);
                Assert.Equal(0, test.State.UnsentAlarmCount);
                test.State.AddSample(1, test.Scheduler.Now, 27, 1, 1);
                await test.Advance(2);
                await test.Stop(loop);

                var rows = test.ReadLogs();
                Assert.Equal(new long[] { 1, 2 }, rows.Where(row => (string)row["EventKind"] == "SampleBufferDropped")
                    .Select(row => (long)row["Count"]));
                foreach (var kind in new[] { "AlarmQueueRejected", "AlarmUnsentHeld", "AlarmUnsentRequeued" })
                {
                    var row = Assert.Single(rows.Where(entry => (string)entry["EventKind"] == kind));
                    Assert.Equal(kind == "AlarmUnsentHeld" ? requeued.EventSequence : 1, (long)row["Count"]);
                    if (kind != "AlarmUnsentRequeued")
                        Assert.Equal("Warning", (string)row["@l"]);
                }
            }
        }

        [Theory]
        [InlineData(9, 0)]
        [InlineData(10, 5)]
        [InlineData(19, 5)]
        [InlineData(20, 10)]
        public async Task RunAsync_CompletedTicks_LogsSnapshotEveryTenTicks(int ticks, int expectedRows)
        {
            using (var test = new CollectorLoopTestContext(new Dictionary<string, string>
            {
                ["JudgeIntervalMilliseconds"] = "100"
            }))
            {
                test.State.AddSample(1, TimeSpan.Zero, 25, 1, 1);
                Task loop = test.StartJudge();
                await test.Advance(ticks / 10.0);
                await test.Stop(loop);

                var snapshots = test.ReadLogs().Where(row => (string)row["EventKind"] == "StatusSnapshot").ToArray();
                Assert.Equal(expectedRows, snapshots.Length);
                Assert.Equal(Enumerable.Range(0, expectedRows).Select(i => i % 5 + 1),
                    snapshots.Select(row => (int)row["EquipmentId"]));
                Assert.All(snapshots, row => Assert.Equal((int)row["EquipmentId"] == 1 ? 1L : 0L, (long)row["Count"]));
            }
        }
    }
}
