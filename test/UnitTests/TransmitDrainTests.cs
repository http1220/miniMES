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
    public class TransmitDrainTests
    {
        [Fact]
        public async Task Drain_SendsAlarmsAndSamplesAndReturnsWithoutIdleDelay()
        {
            using (var test = new CollectorLoopTestContext())
            {
                var client = new FakeServerClient(test.Scheduler);
                var alarm = test.State.SubmitStatusReport(1, AlarmCause.Communication, true, TimeSpan.Zero).Item;
                var sample = test.State.AddSample(1, TimeSpan.Zero, null, null, null);
                var drain = Start(test, client, CancellationToken.None);

                Assert.True(drain.IsCompleted);
                await drain;
                Assert.Equal(new[] { "Alarm", "Sample" }, client.Calls.Select(call => call.Kind));
                Assert.Same(alarm, Assert.Single(client.Calls[0].Alarms).Item);
                Assert.Same(sample, Assert.Single(client.Calls[1].Samples).Item);
                Assert.Equal(0, test.State.AlarmCount);
                Assert.Equal(0, test.State.SampleCount);
            }
        }

        [Fact]
        public async Task Drain_RequeuesHeldAlarmsWhenSpaceBecomesAvailable()
        {
            using (var test = new CollectorLoopTestContext(new Dictionary<string, string>
            {
                ["AlarmQueueCapacity"] = "1"
            }))
            {
                var client = new FakeServerClient(test.Scheduler);
                var queued = test.State.SubmitStatusReport(1, AlarmCause.Communication, true, TimeSpan.Zero).Item;
                var held = test.State.SubmitStatusReport(2, AlarmCause.Communication, true, TimeSpan.Zero).Item;
                Assert.Same(held, Assert.Single(test.State.GetUnsentSnapshot()));

                var drain = Start(test, client, CancellationToken.None);
                Assert.Same(queued, Assert.Single(Assert.Single(client.Calls).Alarms).Item);
                Assert.False(drain.IsCompleted);
                await test.Advance(1);

                Assert.True(drain.IsCompleted);
                await drain;
                Assert.Equal(2, client.Calls.Count);
                Assert.Same(held, Assert.Single(client.Calls[1].Alarms).Item);
                Assert.Equal(0, test.State.AlarmCount);
                Assert.Equal(0, test.State.UnsentAlarmCount);
            }
        }

        [Fact]
        public async Task Drain_FailedRequestsBackOffUntilCanceled()
        {
            using (var test = new CollectorLoopTestContext(new Dictionary<string, string>
            {
                ["BackoffMaximumJitterFraction"] = "0"
            }))
            using (var cancellation = new CancellationTokenSource())
            {
                var client = new FakeServerClient(test.Scheduler);
                client.Respond = (call, ct) => Task.FromResult(new ServerResult(ServerOutcome.ServerError, statusCode: 500));
                var sample = test.State.AddSample(1, TimeSpan.Zero, null, null, null);
                var drain = Start(test, client, cancellation.Token);
                await test.Advance(6);
                Assert.Equal(new[] { 0, 1, 3 }, client.Calls.Select(call => (int)call.At.TotalSeconds));
                Assert.False(drain.IsCompleted);

                cancellation.Cancel();
                await test.Advance(0);
                Assert.True(drain.IsCompleted);
                await drain;
                Assert.Same(sample, Assert.Single(test.State.GetSampleSnapshot()));
            }
        }

        [Fact]
        public async Task Drain_NoReference_WaitsUntilCanceledWithoutDroppingItems()
        {
            using (var test = new CollectorLoopTestContext())
            using (var cancellation = new CancellationTokenSource())
            {
                var client = new FakeServerClient(test.Scheduler);
                var sample = test.State.AddSample(1, TimeSpan.Zero, null, null, null);
                var drain = Start(test, client, cancellation.Token, withReference: false);
                await test.Advance(6);
                Assert.False(drain.IsCompleted);
                Assert.Empty(client.Calls);

                cancellation.Cancel();
                await test.Advance(0);
                Assert.True(drain.IsCompleted);
                await drain;
                Assert.Same(sample, Assert.Single(test.State.GetSampleSnapshot()));
            }
        }

        private static Task Start(CollectorLoopTestContext test, FakeServerClient client, CancellationToken token,
            bool withReference = true)
        {
            var timeline = new TransmissionTimeline(test.Settings.ClockJumpThreshold);
            if (withReference)
                timeline.UpdateReference(TimeSpan.Zero, FakeServerClient.Epoch);
            var loop = new TransmitLoop(test.Settings, test.State, timeline, client, test.Scheduler.NowProvider,
                test.Scheduler.DelayProvider, test.Log, () => 0);
            return test.Scheduler.RunAsync(() => loop.DrainAsync(token));
        }
    }
}
