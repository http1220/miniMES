using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Collector;
using Core;
using Xunit;

namespace UnitTests
{
    public class TransmitLoopTests
    {
        [Fact]
        public async Task RunAsync_WaitsForReference_ThenSendsAlarmBeforeSample()
        {
            using (var test = new CollectorLoopTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                var client = new FakeServerClient(test.Scheduler);
                var timeline = new TransmissionTimeline(test.Settings.ClockJumpThreshold);
                var sample = AddSample(test, 1);
                var alarm = test.State.SubmitStatusReport(1, AlarmCause.Communication, true, Seconds(2)).Item;
                Task loop = Start(test, timeline, client, cancellation.Token);
                await test.Advance(3);
                Assert.Empty(client.Calls);

                timeline.UpdateReference(Seconds(3), FakeServerClient.Epoch);
                await test.Advance(1);
                Assert.Equal(new[] { "Alarm", "Sample" }, client.Calls.Select(call => call.Kind));
                Assert.All(client.Calls, call =>
                {
                    Assert.Equal(Seconds(4), call.At);
                    Assert.Equal(test.State.RunId, call.RunId);
                    Assert.Equal(FakeServerClient.Epoch, call.StartedAtUtc.Value);
                });
                Assert.Same(alarm, Assert.Single(client.Calls[0].Alarms).Item);
                var confirmed = Assert.Single(client.Calls[1].Samples);
                Assert.Same(sample, confirmed.Item);
                Assert.Equal(FakeServerClient.Epoch - Seconds(2), confirmed.CollectedAtUtc);
                Assert.Empty(test.State.GetAlarmSnapshot());
                Assert.Empty(test.State.GetSampleSnapshot());
                await Stop(test, cancellation, loop);
            }
        }

        [Fact]
        public async Task RunAsync_FullBatchesYieldAtSameTime_PartialBatchWaits()
        {
            using (var test = new CollectorLoopTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                var client = new FakeServerClient(test.Scheduler);
                var expected = Enumerable.Range(1, 60).Select(i => AddSample(test, 0)).ToArray();
                Task loop = Start(test, WithReference(test), client, cancellation.Token);
                Assert.Single(client.Calls);
                await test.Advance(0);
                Assert.Equal(2, client.Calls.Count);
                await test.Advance(0);
                Assert.Equal(new[] { 25, 25, 10 }, client.Calls.Select(call => call.Samples.Count));
                Assert.All(client.Calls, call => Assert.Equal(TimeSpan.Zero, call.At));
                Assert.Equal(expected, client.Calls.SelectMany(call => call.Samples).Select(sample => sample.Item));

                var added = AddSample(test, 0);
                await test.Advance(0.999);
                Assert.Equal(3, client.Calls.Count);
                await test.Advance(0.001);
                Assert.Same(added, Assert.Single(client.Calls[3].Samples).Item);
                await Stop(test, cancellation, loop);
            }
        }

        [Fact]
        public async Task RunAsync_SuccessRemovesSentKeysAndUsesResponseClockForNewSamples()
        {
            using (var test = new CollectorLoopTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                var client = new FakeServerClient(test.Scheduler);
                var pending = new TaskCompletionSource<ServerResult>();
                client.Respond = (call, ct) => client.Calls.Count == 1 ? pending.Task : Task.FromResult(client.Success());
                AddSample(test, 0);
                Task loop = Start(test, WithReference(test), client, cancellation.Token);
                await test.Advance(2);
                var added = AddSample(test, 2);
                var serverUtc = FakeServerClient.Epoch + Seconds(100);
                pending.SetResult(new ServerResult(ServerOutcome.Success, Seconds(1), serverUtc));
                await test.Advance(0);
                Assert.Same(added, Assert.Single(test.State.GetSampleSnapshot()));

                await test.Advance(1);
                var next = Assert.Single(client.Calls[1].Samples);
                Assert.Same(added, next.Item);
                Assert.Equal(serverUtc + Seconds(1), next.CollectedAtUtc);
                await Stop(test, cancellation, loop);
                var logs = test.ReadLogs();
                Assert.Contains(logs, row => (string)row["EventKind"] == "SampleStored" && (long)row["Count"] == 1);
                Assert.Contains(logs, row => (string)row["EventKind"] == "ClockJump" &&
                    (string)row["Detail"] == "Forward" && (long)row["Count"] == 99000);
            }
        }

        [Fact]
        public async Task RunAsync_RetriesWithStableKeysAndUtc_ThenResetsBackoff()
        {
            using (var test = new CollectorLoopTestContext(new Dictionary<string, string>
            {
                ["BackoffMaximumJitterFraction"] = "0"
            }))
            using (var cancellation = new CancellationTokenSource())
            {
                var client = new FakeServerClient(test.Scheduler);
                client.Respond = (call, ct) => Task.FromResult(client.Calls.Count == 4
                    ? new ServerResult(ServerOutcome.Success, call.At, FakeServerClient.Epoch + Seconds(100))
                    : new ServerResult(ServerOutcome.ServerError, statusCode: 500));
                var sample = AddSample(test, 0);
                var timeline = WithReference(test);
                Task loop = Start(test, timeline, client, cancellation.Token);
                await test.Advance(6);
                Assert.Same(sample, Assert.Single(test.State.GetSampleSnapshot()));
                Assert.Equal(new[] { 0, 1, 3 }.Select(i => Seconds(i)), client.Calls.Select(call => call.At));

                // Updating the reference while a retry is queued must preserve this item's UTC.
                timeline.UpdateReference(Seconds(6), FakeServerClient.Epoch + Seconds(50));
                await test.Advance(1);
                Assert.Empty(test.State.GetSampleSnapshot());
                Assert.All(client.Calls, call =>
                {
                    var sent = Assert.Single(call.Samples);
                    Assert.Same(sample, sent.Item);
                    Assert.Equal(FakeServerClient.Epoch, sent.CollectedAtUtc);
                });
                AddSample(test, 7);
                await test.Advance(2);
                Assert.Equal(new[] { 0, 1, 3, 7, 8, 9 }.Select(i => Seconds(i)), client.Calls.Select(call => call.At));
                await Stop(test, cancellation, loop);
                var logs = test.ReadLogs();
                var recovery = Assert.Single(logs, row => (string)row["EventKind"] == "TransmitRecovered");
                Assert.Equal(3L, (long)recovery["Count"]);
                Assert.Contains(logs, row => (string)row["EventKind"] == "TransmitFailed" &&
                    (string)row["FailureType"] == "ServerError" && (long)row["Count"] == 500 &&
                    (string)row["Detail"] == "Sample");
            }
        }

        [Theory]
        [InlineData(ServerOutcome.AuthFailed, 401)]
        [InlineData(ServerOutcome.Timeout, null)]
        [InlineData(ServerOutcome.ConnectionFailed, null)]
        [InlineData(ServerOutcome.CertificateFailed, null)]
        public async Task RunAsync_OtherFailuresKeepKeysAndBackOff(ServerOutcome outcome, int? statusCode)
        {
            using (var test = new CollectorLoopTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                var client = new FakeServerClient(test.Scheduler);
                client.Respond = (call, ct) => Task.FromResult(new ServerResult(outcome, statusCode: statusCode));
                var sample = AddSample(test, 0);
                Task loop = Start(test, WithReference(test), client, cancellation.Token);
                await test.Advance(2);
                Assert.Equal(2, client.Calls.Count);
                Assert.Same(sample, Assert.Single(test.State.GetSampleSnapshot()));
                await test.Advance(1);
                Assert.Equal(new[] { 0, 1, 3 }.Select(i => Seconds(i)), client.Calls.Select(call => call.At));
                await Stop(test, cancellation, loop);
            }
        }

        [Theory]
        [InlineData("Alarm", "invalid_field")]
        [InlineData("Sample", null)]
        public async Task RunAsync_BadRequestDiscardsOnlyItsBatchAndContinues(string discardedKind, string errorKind)
        {
            using (var test = new CollectorLoopTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                var client = new FakeServerClient(test.Scheduler);
                var pending = new TaskCompletionSource<ServerResult>();
                client.Respond = (call, ct) => call.At == TimeSpan.Zero && call.Kind == discardedKind
                    ? pending.Task : Task.FromResult(client.Success());
                AddSample(test, 0);
                test.State.SubmitStatusReport(1, AlarmCause.Communication, true, TimeSpan.Zero);
                Task loop = Start(test, WithReference(test), client, cancellation.Token);
                var added = AddSample(test, 0);
                pending.SetResult(new ServerResult(ServerOutcome.BadRequest, statusCode: 400, errorKind: errorKind));
                await test.Advance(0);
                Assert.Equal(new[] { "Alarm", "Sample" }, client.Calls.Select(call => call.Kind));
                Assert.Empty(test.State.GetAlarmSnapshot());
                Assert.Same(added, Assert.Single(test.State.GetSampleSnapshot()));
                await test.Advance(1);
                Assert.Same(added, Assert.Single(client.Calls[2].Samples).Item);
                await Stop(test, cancellation, loop);
                var logs = test.ReadLogs();
                var discarded = Assert.Single(logs, row => (string)row["EventKind"] == "BatchDiscarded");
                Assert.Equal("Error", (string)discarded["@l"]);
                Assert.Equal(discardedKind, (string)discarded["Detail"]);
                Assert.Equal(errorKind ?? "BadRequest", (string)discarded["FailureType"]);
                Assert.Equal(1L, (long)discarded["Count"]);
                Assert.DoesNotContain(logs, row => (string)row["EventKind"] == "TransmitFailed");
                Assert.DoesNotContain(logs, row => (string)row["EventKind"] == "TransmitRecovered");
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RunAsync_AlarmFailureStillSendsSample_BackoffRequiresEveryRequestToFail(bool sampleFails)
        {
            using (var test = new CollectorLoopTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                var client = new FakeServerClient(test.Scheduler);
                client.Respond = (call, ct) => Task.FromResult(call.Kind == "Alarm" || sampleFails
                    ? new ServerResult(ServerOutcome.ServerError, statusCode: 500) : client.Success());

                var alarm = test.State.SubmitStatusReport(1, AlarmCause.Communication, true, TimeSpan.Zero).Item;
                var sample = AddSample(test, 0);
                Task loop = Start(test, WithReference(test), client, cancellation.Token);
                Assert.Equal(new[] { "Alarm", "Sample" }, client.Calls.Select(call => call.Kind));
                Assert.Same(alarm, Assert.Single(test.State.GetAlarmSnapshot()));
                if (sampleFails)
                    Assert.Same(sample, Assert.Single(test.State.GetSampleSnapshot()));
                else
                    Assert.Empty(test.State.GetSampleSnapshot());

                await test.Advance(2);
                Assert.Equal(sampleFails ? new[] { 0, 1 } : new[] { 0, 1, 2 },
                    client.Calls.Where(call => call.Kind == "Alarm").Select(call => (int)call.At.TotalSeconds));
                await Stop(test, cancellation, loop);
            }
        }

        [Fact]
        public async Task RunAsync_BufferEvictionDuringSend_IgnoresEvictedKeyAndKeepsNewKey()
        {
            using (var test = new CollectorLoopTestContext(new Dictionary<string, string> { ["SampleBufferCapacity"] = "25" }))
            using (var cancellation = new CancellationTokenSource())
            {
                var client = new FakeServerClient(test.Scheduler);
                var pending = new TaskCompletionSource<ServerResult>();
                client.Respond = (call, ct) => pending.Task;
                for (int i = 0; i < 25; i++)
                    AddSample(test, 0);
                Task loop = Start(test, WithReference(test), client, cancellation.Token);
                Assert.Equal(25, Assert.Single(client.Calls).Samples.Count);
                // The new sample shares the evicted sample's sequence number on a different device.
                var added = test.State.AddSample(2, Seconds(1), null, null, null);
                Assert.Equal(client.Calls[0].Samples[0].Item.SequenceNumber, added.SequenceNumber);
                Assert.Equal(1L, test.State.SamplesDropped);
                await test.Advance(1);
                pending.SetResult(client.Success());
                await test.Advance(0);
                Assert.Single(client.Calls);
                Assert.Same(added, Assert.Single(test.State.GetSampleSnapshot()));
                Assert.Equal(1L, test.State.SamplesDropped);
                await Stop(test, cancellation, loop);
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
                AddSample(test, 0);
                Task loop = Start(test, WithReference(test), client, cancellation.Token);
                Assert.Single(client.Calls);
                await Stop(test, cancellation, loop);
                Assert.Equal(TaskStatus.RanToCompletion, loop.Status);
                Assert.Single(test.State.GetSampleSnapshot());
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
                AddSample(test, 0);
                Task loop = Start(test, WithReference(test), client, CancellationToken.None);
                var actual = await Record.ExceptionAsync(() => loop);
                Assert.IsType(error.GetType(), actual);
            }
        }

        private static SampleItem AddSample(CollectorLoopTestContext test, int seconds) =>
            test.State.AddSample(1, Seconds(seconds), null, null, null);

        private static TimeSpan Seconds(int seconds) => TimeSpan.FromSeconds(seconds);

        private static TransmissionTimeline WithReference(CollectorLoopTestContext test)
        {
            var timeline = new TransmissionTimeline(test.Settings.ClockJumpThreshold);
            timeline.UpdateReference(TimeSpan.Zero, FakeServerClient.Epoch);
            return timeline;
        }

        private static Task Start(CollectorLoopTestContext test, TransmissionTimeline timeline,
            FakeServerClient client, CancellationToken ct)
        {
            var loop = new TransmitLoop(test.Settings, test.State, timeline, client,
                test.Scheduler.NowProvider, test.Scheduler.DelayProvider, test.Log, () => 0);
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
