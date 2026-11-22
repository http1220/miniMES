using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace UnitTests
{
    public class VirtualSchedulerTests
    {
        [Fact]
        public async Task AdvanceAsync_RepeatedPeriodicLoop_TicksTenTimes()
        {
            for (var trial = 0; trial < 50; trial++)
            {
                var scheduler = new VirtualScheduler();
                var counter = 0;
                using (var cancellation = new CancellationTokenSource())
                {
                    var loop = scheduler.RunAsync(async () =>
                    {
                        while (true)
                        {
                            await scheduler.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
                            counter++;
                        }
                    });
                    await scheduler.AdvanceAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(10, counter);
                    cancellation.Cancel();
                    await scheduler.AdvanceAsync(TimeSpan.Zero);
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
                }
            }
        }

        [Fact]
        public async Task AdvanceAsync_TwoPeriodicLoops_WakesInDueAndRegistrationOrder()
        {
            var scheduler = new VirtualScheduler();
            var events = new List<string>();
            async Task Record(int interval, int count)
            {
                for (var i = 0; i < count; i++)
                {
                    await scheduler.DelayProvider(TimeSpan.FromSeconds(interval), CancellationToken.None);
                    events.Add(interval + "@" + scheduler.NowProvider().TotalSeconds);
                }
            }

            var fast = scheduler.RunAsync(() => Record(1, 6));
            var slow = scheduler.RunAsync(() => Record(3, 2));
            await scheduler.AdvanceAsync(TimeSpan.FromSeconds(6));
            Assert.Equal(new[] { "1@1", "1@2", "3@3", "1@3", "1@4", "1@5", "3@6", "1@6" }, events);
            await Task.WhenAll(fast, slow);
        }

        [Fact]
        public async Task Delay_CancelledToken_RemovesPendingWait()
        {
            var scheduler = new VirtualScheduler();
            using (var cancellation = new CancellationTokenSource())
            {
                var delay = scheduler.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delay);
                var alreadyCancelled = scheduler.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
                Assert.True(alreadyCancelled.IsCanceled);
                await scheduler.AdvanceAsync(TimeSpan.FromSeconds(2));
                Assert.True(delay.IsCanceled);
            }
        }

        [Fact]
        public async Task AdvanceAsync_ContinuationRegistersMoreDelays_ProcessesThemAtMonotonicDueTimes()
        {
            var scheduler = new VirtualScheduler();
            var times = new List<TimeSpan>();
            var loop = scheduler.RunAsync(async () =>
            {
                foreach (var milliseconds in new[] { 100, 200, 300 })
                {
                    await scheduler.Delay(TimeSpan.FromMilliseconds(milliseconds), CancellationToken.None);
                    times.Add(scheduler.Now);
                }
            });
            await scheduler.AdvanceAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(new[] { TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300),
                TimeSpan.FromMilliseconds(600) }, times);
            Assert.Equal(TimeSpan.FromSeconds(1), scheduler.Now);
            await loop;
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task Delay_NonPositiveDuration_CompletesOnNextAdvance(int milliseconds)
        {
            var scheduler = new VirtualScheduler();
            var delay = scheduler.Delay(TimeSpan.FromMilliseconds(milliseconds), CancellationToken.None);
            Assert.False(delay.IsCompleted);
            await scheduler.AdvanceAsync(TimeSpan.Zero);
            Assert.True(delay.IsCompleted);
        }
    }
}
