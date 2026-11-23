using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Collector;
using Xunit;

namespace UnitTests
{
    public class HeartbeatLoopTests
    {
        [Fact]
        public async Task RunAsync_SendsImmediatelyAndKeepsPeriodFromCycleStart()
        {
            using (var test = new CollectorLoopTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                var timeline = new TransmissionTimeline(test.Settings.ClockJumpThreshold);
                var client = new FakeServerClient(test.Scheduler);
                client.Respond = async (call, ct) =>
                {
                    await test.Scheduler.Delay(TimeSpan.FromSeconds(0.5), ct);
                    return client.Success();
                };

                Task loop = Start(test, timeline, client, cancellation.Token);
                Assert.Equal(TimeSpan.Zero, Assert.Single(client.Calls).At);
                Assert.False(timeline.HasReference);
                await test.Advance(0.5);
                Assert.True(timeline.HasReference);
                Assert.Equal(FakeServerClient.Epoch.AddSeconds(0.5), timeline.StartedAtUtc.Value);

                await test.Advance(5.5);
                Assert.Equal(new[] { 0, 2, 4, 6 }.Select(i => TimeSpan.FromSeconds(i)), client.Calls.Select(call => call.At));
                Assert.All(client.Calls, call =>
                {
                    Assert.Equal("Heartbeat", call.Kind);
                    Assert.Equal(test.State.RunId, call.RunId);
                });
                Assert.Equal(FakeServerClient.Epoch.AddSeconds(0.5), timeline.StartedAtUtc.Value);
                await Stop(test, cancellation, loop);
            }
        }

        [Fact]
        public async Task RunAsync_DiscardsFailuresAndLogsTransitionsOnce()
        {
            using (var test = new CollectorLoopTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                var timeline = new TransmissionTimeline(test.Settings.ClockJumpThreshold);
                var client = new FakeServerClient(test.Scheduler);
                client.Respond = (call, ct) => Task.FromResult(client.Calls.Count <= 3
                    ? new ServerResult(client.Calls.Count == 1 ? ServerOutcome.Timeout : ServerOutcome.ServerError)
                    : client.Success());

                Task loop = Start(test, timeline, client, cancellation.Token);
                await test.Advance(5);
                Assert.False(timeline.HasReference);
                Assert.Equal(3, client.Calls.Count);
                await test.Advance(3);
                Assert.Equal(new[] { 0, 2, 4, 6, 8 }.Select(i => TimeSpan.FromSeconds(i)), client.Calls.Select(call => call.At));
                Assert.Equal(FakeServerClient.Epoch.AddSeconds(6), timeline.StartedAtUtc.Value);
                await Stop(test, cancellation, loop);

                var logs = test.ReadLogs();
                var failed = Assert.Single(logs, row => (string)row["EventKind"] == "HeartbeatFailed");
                Assert.Equal("Warning", (string)failed["@l"]);
                Assert.Equal("Timeout", (string)failed["FailureType"]);
                Assert.Single(logs, row => (string)row["EventKind"] == "HeartbeatRecovered");
            }
        }

        [Fact]
        public async Task RunAsync_ClockJumpsLogDirectionAndSignedMilliseconds()
        {
            using (var test = new CollectorLoopTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                var timeline = new TransmissionTimeline(test.Settings.ClockJumpThreshold);
                var client = new FakeServerClient(test.Scheduler);
                var offsets = new[] { 0, 5, -3 };
                client.Respond = (call, ct) => Task.FromResult(new ServerResult(ServerOutcome.Success, call.At,
                    FakeServerClient.Epoch + call.At + TimeSpan.FromSeconds(offsets[client.Calls.Count - 1])));

                Task loop = Start(test, timeline, client, cancellation.Token);
                await test.Advance(4);
                await Stop(test, cancellation, loop);
                var jumps = test.ReadLogs().Where(row => (string)row["EventKind"] == "ClockJump").ToArray();
                Assert.Equal(new[] { "Forward", "Backward" }, jumps.Select(row => (string)row["Detail"]));
                Assert.Equal(new[] { 5000L, -8000L }, jumps.Select(row => (long)row["Count"]));
                Assert.All(jumps, row => Assert.Equal("Warning", (string)row["@l"]));
            }
        }

        [Fact]
        public async Task RunAsync_CanceledDuringRequest_ReturnsNormally()
        {
            using (var test = new CollectorLoopTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                var client = new FakeServerClient(test.Scheduler);
                client.Respond = (call, ct) =>
                {
                    var pending = new TaskCompletionSource<ServerResult>();
                    ct.Register(() => pending.TrySetCanceled());
                    return pending.Task;
                };

                Task loop = Start(test, new TransmissionTimeline(test.Settings.ClockJumpThreshold), client, cancellation.Token);
                Assert.Single(client.Calls);
                await Stop(test, cancellation, loop);
                Assert.Equal(TaskStatus.RanToCompletion, loop.Status);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RunAsync_UnexpectedExceptionPropagates(bool unrelatedCancellation)
        {
            using (var test = new CollectorLoopTestContext())
            {
                var client = new FakeServerClient(test.Scheduler);
                Exception error = unrelatedCancellation
                    ? (Exception)new OperationCanceledException() : new InvalidOperationException();
                client.Respond = (call, ct) => throw error;

                Task loop = Start(test, new TransmissionTimeline(test.Settings.ClockJumpThreshold), client, CancellationToken.None);
                var actual = await Record.ExceptionAsync(() => loop);
                Assert.IsType(error.GetType(), actual);
            }
        }

        private static Task Start(CollectorLoopTestContext test, TransmissionTimeline timeline,
            FakeServerClient client, CancellationToken ct)
        {
            var loop = new HeartbeatLoop(test.Settings, test.State, timeline, client,
                test.Scheduler.NowProvider, test.Scheduler.DelayProvider, test.Log);
            return test.Scheduler.RunAsync(() => loop.RunAsync(ct));
        }

        private static async Task Stop(CollectorLoopTestContext test, CancellationTokenSource cancellation, Task loop)
        {
            cancellation.Cancel();
            await test.Advance(0);
            await loop;
        }
    }
}
