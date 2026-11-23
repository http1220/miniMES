using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Collector;
using Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace UnitTests
{
    public class SupervisedRuntimeTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task EquipmentFailure_StopsSiblingsAndLogsFatal(bool unexpectedCancellation)
        {
            using (var test = new CollectorLoopTestContext())
            using (var shutdown = new CancellationTokenSource())
            {
                var client = new FakeServerClient(test.Scheduler);
                var readers = NewReaders(test);
                readers[3].Enqueue(async (timeout, ct) =>
                {
                    await test.Scheduler.Delay(Seconds(1), ct);
                    if (unexpectedCancellation)
                        throw new OperationCanceledException("private text");
                    throw new InvalidOperationException("private text");
                });

                var run = Start(test, shutdown.Token, readers, client);
                await test.Advance(1);
                Assert.True(run.IsCompleted);
                Assert.Equal(ExitCodes.FatalLoop, await run);
                Assert.True(test.Scheduler.Now < Seconds(2));
                var polls = readers.Values.Sum(reader => reader.Calls.Count);
                var heartbeats = client.Calls.Count(call => call.Kind == "Heartbeat");
                await test.Advance(10);
                Assert.Equal(polls, readers.Values.Sum(reader => reader.Calls.Count));
                Assert.Equal(1, heartbeats);
                Assert.Equal(heartbeats, client.Calls.Count(call => call.Kind == "Heartbeat"));

                var fatal = Assert.Single(Events(test.ReadLogs(), "LoopFatal"));
                Assert.Equal("Fatal", (string)fatal["@l"]);
                Assert.Equal("Equipment.3", (string)fatal["Detail"]);
                Assert.Equal(unexpectedCancellation ? "UnexpectedCancel" : null, (string)fatal["FailureType"]);
                Assert.Equal(unexpectedCancellation ? null : typeof(InvalidOperationException).FullName,
                    (string)fatal["ExceptionType"]);
                Assert.DoesNotContain("private text", fatal.ToString());
            }
        }

        [Fact]
        public async Task TransmitFailure_IsFatal()
        {
            using (var test = new CollectorLoopTestContext())
            using (var shutdown = new CancellationTokenSource())
            {
                var client = new FakeServerClient(test.Scheduler);
                client.Respond = (call, ct) => call.Kind == "Heartbeat"
                    ? Task.FromResult(client.Success())
                    : throw new InvalidOperationException();
                var run = Start(test, shutdown.Token, NewReaders(test), client);
                await test.Advance(1);
                Assert.True(run.IsCompleted);
                Assert.Equal(ExitCodes.FatalLoop, await run);
                var fatal = Assert.Single(Events(test.ReadLogs(), "LoopFatal"));
                Assert.Equal("Transmit", (string)fatal["Detail"]);
                Assert.Equal(typeof(InvalidOperationException).FullName, (string)fatal["ExceptionType"]);
            }
        }

        [Theory]
        [InlineData(0.5)]
        [InlineData(3)]
        public async Task FatalCleanup_ObservesSiblingFailuresWithinAndAfterGracePeriod(double siblingFailureTime)
        {
            using (var test = new CollectorLoopTestContext())
            {
                var readers = NewReaders(test);
                var siblingFinished = false;
                readers[1].Enqueue(async (timeout, ct) =>
                {
                    await test.Scheduler.Delay(Seconds(siblingFailureTime), CancellationToken.None);
                    Assert.True(ct.IsCancellationRequested);
                    siblingFinished = true;
                    throw new FormatException();
                });
                readers[3].Enqueue(() => throw new InvalidOperationException());

                var run = Start(test, CancellationToken.None, readers);
                Assert.False(run.IsCompleted);
                await test.Advance(Math.Min(siblingFailureTime, 2));
                Assert.True(run.IsCompleted);
                Assert.Equal(ExitCodes.FatalLoop, await run);
                Assert.Equal(siblingFailureTime < 2, siblingFinished);
                await test.Advance(3);
                Assert.True(siblingFinished);

                var logs = test.ReadLogs();
                Assert.Single(Events(logs, "LoopFatal"));
                var observed = Assert.Single(Events(logs, "ExceptionObserved"));
                Assert.Equal("Warning", (string)observed["@l"]);
                Assert.Equal("Equipment.1", (string)observed["Detail"]);
                Assert.Equal(typeof(FormatException).FullName, (string)observed["ExceptionType"]);
            }
        }

        [Fact]
        public async Task Shutdown_StopsPollingAndHeartbeatBeforeDraining()
        {
            using (var test = new CollectorLoopTestContext())
            using (var shutdown = new CancellationTokenSource())
            {
                var client = new FakeServerClient(test.Scheduler);
                var readers = NewReaders(test);
                var pollStopped = false;
                readers[1].Enqueue(async (timeout, ct) =>
                {
                    await test.Scheduler.Delay(Seconds(0.5), CancellationToken.None);
                    pollStopped = ct.IsCancellationRequested;
                    ct.ThrowIfCancellationRequested();
                    return readers[1].Success();
                });
                var alarm = test.State.SubmitStatusReport(1, AlarmCause.Temperature, true, TimeSpan.Zero).Item;
                client.Respond = (call, ct) =>
                {
                    if (call.Kind != "Heartbeat")
                        Assert.True(pollStopped);
                    return Task.FromResult(client.Success());
                };

                var run = Start(test, shutdown.Token, readers, client);
                shutdown.Cancel();
                await test.Advance(0);
                Assert.False(run.IsCompleted);
                Assert.Equal("Heartbeat", Assert.Single(client.Calls).Kind);
                await test.Advance(0.5);
                Assert.True(run.IsCompleted);
                Assert.Equal(ExitCodes.Success, await run);
                Assert.True(pollStopped);
                Assert.Equal(new[] { "Heartbeat", "Alarm", "Sample" }, client.Calls.Select(call => call.Kind));
                Assert.Same(alarm, Assert.Single(client.Calls[1].Alarms).Item);
                Assert.NotEmpty(client.Calls[2].Samples);
                Assert.Equal(0, Remaining(test));
                await test.Advance(10);
                Assert.All(readers.Values, reader => Assert.Single(reader.Calls));
                Assert.Equal(3, client.Calls.Count);
                Assert.Equal(new[] { "ShutdownRequested", "ShutdownCompleted" },
                    ShutdownEvents(test.ReadLogs()).Select(row => (string)row["EventKind"]));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Shutdown_UsesOneDeadlineAndCancelsPendingRequests(bool blockedDrain)
        {
            using (var test = WithTwoSecondTimeout())
            using (var shutdown = new CancellationTokenSource())
            {
                var client = new FakeServerClient(test.Scheduler);
                var readers = NewReaders(test);
                readers[1].Enqueue(async (timeout, ct) =>
                {
                    await test.Scheduler.Delay(Seconds(2.5), CancellationToken.None);
                    ct.ThrowIfCancellationRequested();
                    return readers[1].Success();
                });
                test.State.SubmitStatusReport(1, AlarmCause.Temperature, true, TimeSpan.Zero);
                test.State.SubmitStatusReport(2, AlarmCause.Temperature, true, TimeSpan.Zero);
                var liveRequestCanceled = false;
                var drainRequestCanceled = false;
                client.Respond = (call, ct) =>
                {
                    if (call.Kind == "Heartbeat")
                        return Task.FromResult(client.Success());
                    if (shutdown.IsCancellationRequested && !blockedDrain)
                        return Task.FromResult(new ServerResult(ServerOutcome.ServerError, statusCode: 500));

                    var response = new TaskCompletionSource<ServerResult>();
                    var draining = shutdown.IsCancellationRequested;
                    ct.Register(() =>
                    {
                        if (draining)
                            drainRequestCanceled = true;
                        else
                            liveRequestCanceled = true;
                        response.TrySetCanceled();
                    });
                    return response.Task;
                };

                var run = Start(test, shutdown.Token, readers, client);
                await test.Advance(1);
                Assert.Single(client.Calls, call => call.Kind != "Heartbeat");
                shutdown.Cancel();
                await test.Advance(1.999);
                Assert.False(run.IsCompleted);
                Assert.True(liveRequestCanceled);
                Assert.False(drainRequestCanceled);
                Assert.Contains(client.Calls, call => call.Kind != "Heartbeat" && call.At == Seconds(2.5));
                await test.Advance(0.001);
                Assert.Equal(Seconds(3), test.Scheduler.Now);
                Assert.True(run.IsCompleted);
                Assert.Equal(ExitCodes.ShutdownIncomplete, await run);
                Assert.Equal(blockedDrain, drainRequestCanceled);
                Assert.True(test.State.SampleCount > 0);
                Assert.Equal(1, test.State.AlarmCount);
                Assert.True(test.State.UnsentAlarmCount > 0);
                var incomplete = Assert.Single(Events(test.ReadLogs(), "ShutdownIncomplete"));
                Assert.Equal("Warning", (string)incomplete["@l"]);
                Assert.Equal(Remaining(test), (long)incomplete["Count"]);
            }
        }

        [Fact]
        public async Task Shutdown_PollStillPendingAtDeadline_DoesNotStartDrain()
        {
            using (var test = WithTwoSecondTimeout())
            using (var shutdown = new CancellationTokenSource())
            {
                var client = new FakeServerClient(test.Scheduler);
                var readers = NewReaders(test);
                readers[1].Enqueue(async (timeout, ct) =>
                {
                    await test.Scheduler.Delay(Seconds(3), CancellationToken.None);
                    ct.ThrowIfCancellationRequested();
                    return readers[1].Success();
                });

                var run = Start(test, shutdown.Token, readers, client);
                shutdown.Cancel();
                await test.Advance(2);
                Assert.True(run.IsCompleted);
                Assert.Equal(ExitCodes.ShutdownIncomplete, await run);
                Assert.Equal("Heartbeat", Assert.Single(client.Calls).Kind);
                await test.Advance(1);
                Assert.Equal("Heartbeat", Assert.Single(client.Calls).Kind);
            }
        }

        [Fact]
        public async Task Shutdown_LoopFaultWhileAnotherPollIsPending_IsFatal()
        {
            using (var test = WithTwoSecondTimeout())
            using (var shutdown = new CancellationTokenSource())
            {
                var readers = NewReaders(test);
                readers[1].Enqueue(async (timeout, ct) =>
                {
                    await test.Scheduler.Delay(Seconds(1), CancellationToken.None);
                    throw new FormatException();
                });
                readers[3].Enqueue(async (timeout, ct) =>
                {
                    await test.Scheduler.Delay(Seconds(0.5), CancellationToken.None);
                    Assert.True(ct.IsCancellationRequested);
                    throw new InvalidOperationException();
                });

                var run = Start(test, shutdown.Token, readers);
                shutdown.Cancel();
                await test.Advance(1);
                Assert.True(run.IsCompleted);
                Assert.Equal(ExitCodes.FatalLoop, await run);
                var logs = test.ReadLogs();
                Assert.Single(Events(logs, "ShutdownRequested"));
                var fatal = Assert.Single(Events(logs, "LoopFatal"));
                Assert.Equal("Equipment.3", (string)fatal["Detail"]);
                Assert.Single(Events(logs, "ExceptionObserved"));
            }
        }

        [Fact]
        public async Task Shutdown_DrainFault_IsFatal()
        {
            using (var test = new CollectorLoopTestContext())
            using (var shutdown = new CancellationTokenSource())
            {
                var client = new FakeServerClient(test.Scheduler);
                client.Respond = (call, ct) => call.Kind == "Heartbeat"
                    ? Task.FromResult(client.Success())
                    : throw new InvalidOperationException();
                var run = Start(test, shutdown.Token, NewReaders(test), client);
                shutdown.Cancel();
                await test.Advance(0);
                Assert.True(run.IsCompleted);
                Assert.Equal(ExitCodes.FatalLoop, await run);
                var log = Assert.Single(Events(test.ReadLogs(), "LoopFatal"));
                Assert.Equal("Transmit", (string)log["Detail"]);
                Assert.Equal(typeof(InvalidOperationException).FullName, (string)log["ExceptionType"]);
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public async Task Shutdown_PreCanceled_SkipsLoopWork(bool buffered, bool withServer)
        {
            using (var test = new CollectorLoopTestContext())
            using (var shutdown = new CancellationTokenSource())
            {
                var readers = NewReaders(test);
                var client = withServer ? new FakeServerClient(test.Scheduler) : null;
                if (buffered)
                    test.State.AddSample(1, TimeSpan.Zero, null, null, null);
                shutdown.Cancel();

                var run = Start(test, shutdown.Token, readers, client);
                Assert.True(run.IsCompleted);
                Assert.Equal(buffered ? ExitCodes.ShutdownIncomplete : ExitCodes.Success, await run);
                Assert.All(readers.Values, reader => Assert.Empty(reader.Calls));
                if (client != null)
                    Assert.Empty(client.Calls);
                var events = ShutdownEvents(test.ReadLogs()).ToArray();
                Assert.Equal(new[] { "ShutdownRequested", buffered ? "ShutdownIncomplete" : "ShutdownCompleted" },
                    events.Select(row => (string)row["EventKind"]));
                if (buffered)
                    Assert.Equal(1L, (long)events[1]["Count"]);
            }
        }

        [Fact]
        public async Task EquipmentLoop_UnrequestedCancellation_Propagates()
        {
            using (var test = new CollectorLoopTestContext())
            {
                var reader = new FakeModbusReader(test.Scheduler);
                reader.Enqueue(() => throw new OperationCanceledException());
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.StartEquipment(reader));
            }
        }

        [Fact]
        public async Task JudgeLoop_UnrequestedCancellation_Propagates()
        {
            using (var test = new CollectorLoopTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                var judge = new JudgeLoop(test.Settings, test.State, test.Scheduler.NowProvider,
                    (interval, ct) => throw new OperationCanceledException(), test.Log);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    test.Scheduler.RunAsync(() => judge.RunAsync(cancellation.Token)));
                Assert.False(cancellation.IsCancellationRequested);
            }
        }

        private static CollectorLoopTestContext WithTwoSecondTimeout() =>
            new CollectorLoopTestContext(new Dictionary<string, string>
            {
                ["ShutdownTimeoutMilliseconds"] = "2000",
                ["BackoffMaximumJitterFraction"] = "0",
                ["AlarmQueueCapacity"] = "1"
            });

        private static Dictionary<int, FakeModbusReader> NewReaders(CollectorLoopTestContext test) =>
            test.Settings.Equipment.ToDictionary(device => device.Id, device =>
            {
                var reader = new FakeModbusReader(test.Scheduler);
                reader.Fallback = (timeout, ct) => Task.FromResult(reader.Success(order: device.WordOrder));
                return reader;
            });

        private static Task<int> Start(CollectorLoopTestContext test, CancellationToken shutdown,
            Dictionary<int, FakeModbusReader> readers, FakeServerClient client = null)
        {
            var runtime = new CollectorRuntime(test.Settings, test.State, test.Log, device => readers[device.Id],
                test.Scheduler.NowProvider, test.Scheduler.DelayProvider, id => () => 0, client,
                client == null ? null : new TransmissionTimeline(test.Settings.ClockJumpThreshold));
            Task<int> run = null;
            test.Scheduler.RunAsync(() => run = runtime.RunSupervisedAsync(shutdown));
            return run;
        }

        private static long Remaining(CollectorLoopTestContext test) =>
            (long)test.State.SampleCount + test.State.AlarmCount + test.State.UnsentAlarmCount;

        private static IEnumerable<JObject> Events(JObject[] logs, string kind) =>
            logs.Where(row => (string)row["EventKind"] == kind);

        private static IEnumerable<JObject> ShutdownEvents(JObject[] logs) =>
            logs.Where(row => ((string)row["EventKind"]).StartsWith("Shutdown", StringComparison.Ordinal));

        private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);
    }
}
