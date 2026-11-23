using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Collector;
using Microsoft.Data.SqlClient;
using Xunit;

namespace IntegrationTests
{
    [CollectionDefinition("Https5443", DisableParallelization = true)]
    public sealed class Https5443Collection
    {
    }

    [Collection("Https5443")]
    public sealed class CollectorServerIntegrationTests
    {
        [Fact]
        [Trait("Category", "Slow")]
        public Task NormalRun_StoresSamplesForAllEquipmentAndShutsDown()
        {
            return WithHarnessAsync(async harness =>
            {
                harness.StartServer();
                var run = harness.StartCollector();
                await WaitForSamplesAsync(harness, run, rows => Enumerable.Range(1, 5)
                    .All(id => rows.Count(row => row.EquipmentId == id) >= 2));

                await run.StopAsync();
                Assert.Equal(1, harness.RunCount(run.Id));
                AssertSampleSequences(await harness.ReadSamplesAsync(run.Id), 2);
            });
        }

        [Fact]
        [Trait("Category", "Slow")]
        public Task Fs15_StoresBufferedSamplesWithOriginalSequenceAndCollectionTime()
        {
            return WithHarnessAsync(async harness =>
            {
                var run = harness.StartCollector();
                var buffered = await WaitForOfflineSamplesAsync(run);
                var serverStartUtc = DateTime.UtcNow;
                harness.StartServer();

                var stored = await WaitForSamplesAsync(harness, run, rows => Enumerable.Range(1, 5)
                    .All(id => rows.Count(row => row.EquipmentId == id) >=
                        buffered.Count(item => item.EquipmentId == id)));
                AssertSampleSequences(stored, 3);
                Assert.Equal(1, harness.RunCount(run.Id));

                var storedByKey = stored.ToDictionary(row => (row.EquipmentId, row.SequenceNumber));
                var firstSuccessUtc = run.FirstSuccessUtc;
                foreach (var group in buffered.GroupBy(item => item.EquipmentId))
                {
                    DateTime? previousUtc = null;
                    foreach (var item in group)
                    {
                        var row = storedByKey[(item.EquipmentId, item.SequenceNumber)];
                        Assert.Equal(item.Temperature, row.Temperature);
                        Assert.True(row.CollectedAtUtc >= run.StartedUtc &&
                            row.CollectedAtUtc < serverStartUtc && row.CollectedAtUtc < firstSuccessUtc,
                            $"Equipment {row.EquipmentId} sequence {row.SequenceNumber}: collection UTC {row.CollectedAtUtc:O}, " +
                            $"Collector start {run.StartedUtc:O}, Server startup {serverStartUtc:O}, first success {firstSuccessUtc:O}.");
                        Assert.InRange(row.CollectedAtUtc - (run.StartedUtc + item.ReceivedAt),
                            TimeSpan.FromMilliseconds(-500), TimeSpan.FromMilliseconds(500));
                        Assert.True(!previousUtc.HasValue || row.CollectedAtUtc > previousUtc.Value,
                            $"Equipment {row.EquipmentId} sequence {row.SequenceNumber}: buffered sample UTC did not advance.");
                        previousUtc = row.CollectedAtUtc;
                    }
                }
            });
        }

        [Fact]
        [Trait("Category", "Slow")]
        public Task Fs12_RestartWithAbnormalTemperature_ReportsOnceAndKeepsActiveAlarm()
        {
            return WithHarnessAsync(harness => CheckRestartAsync(harness, returnToNormal: false));
        }

        [Fact]
        [Trait("Category", "Slow")]
        public Task Fs12_RestartWithNormalTemperature_ReportsAfterConsecutiveSamplesAndClearsAlarm()
        {
            return WithHarnessAsync(harness => CheckRestartAsync(harness, returnToNormal: true));
        }

        [Fact]
        [Trait("Category", "Slow")]
        public Task Fs12_StartWithoutServer_StoresBufferedAbnormalReportWithOriginalTime()
        {
            return WithHarnessAsync(async harness =>
            {
                harness.SetTemperature(100f);
                var run = harness.StartCollector();
                await WaitForOfflineSamplesAsync(run);
                var bufferedReport = await PollAsync(_ => Task.FromResult(run.State.GetAlarmSnapshot()),
                    events => events.Any(IsDeviceOneTemperature),
                    events => $"SAMPLE=0, ALARM_EVENT=0 (server not started), buffered alarms={events.Length}");
                var expected = Assert.Single(bufferedReport.Where(IsDeviceOneTemperature));
                Assert.Equal(AlarmEventType.ReportAbnormal, expected.EventType);
                Assert.False(run.Timeline.HasReference);

                var serverStartUtc = DateTime.UtcNow;
                harness.StartServer();
                await WaitForTemperatureEventsAsync(harness, run);
                await run.StopAsync();
                var report = Assert.Single(await harness.ReadTemperatureEventsAsync(run.Id));
                Assert.Equal((byte)3, report.EventType);
                Assert.Equal(expected.EventSequence, report.EventSequence);
                Assert.Equal(1, harness.RunCount(run.Id));
                Assert.True(report.OccurredAtUtc >= run.StartedUtc && report.OccurredAtUtc < serverStartUtc);
                Assert.True(report.OccurredAtUtc < run.FirstSuccessUtc);
                Assert.InRange(report.OccurredAtUtc - (run.StartedUtc + expected.OccurredAt),
                    TimeSpan.FromMilliseconds(-500), TimeSpan.FromMilliseconds(500));
                var alarm = Assert.Single(harness.ReadTemperatureAlarms());
                Assert.Null(alarm.ClearedAtUtc);
                Assert.Equal((int?)alarm.AlarmId, report.AlarmId);
                Assert.Equal(report.OccurredAtUtc, alarm.RaisedAtUtc);
            });
        }

        private static async Task CheckRestartAsync(Harness harness, bool returnToNormal)
        {
            harness.SetTemperature(100f);
            harness.StartServer();
            var initial = harness.StartCollector();
            await WaitForTemperatureEventsAsync(harness, initial);
            await initial.StopAsync();

            var originalAlarm = Assert.Single(harness.ReadTemperatureAlarms());
            Assert.Null(originalAlarm.ClearedAtUtc);
            Assert.Equal((byte)3, Assert.Single(await harness.ReadTemperatureEventsAsync(initial.Id)).EventType);

            if (returnToNormal)
                harness.SetTemperature(23.456f);

            var restarted = harness.StartCollector();
            Assert.NotEqual(initial.Id, restarted.Id);
            await WaitForTemperatureEventsAsync(harness, restarted);
            await WaitForSamplesAsync(harness, restarted, rows => Enumerable.Range(1, 5)
                .All(id => rows.Count(row => row.EquipmentId == id) >= restarted.Settings.ThresholdConsecutiveCount));
            await restarted.StopAsync();

            Assert.Equal(1, harness.RunCount(initial.Id));
            Assert.Equal(1, harness.RunCount(restarted.Id));
            var sequences = harness.ReadEventSequences(restarted.Id);
            Assert.Equal(Enumerable.Range(1, sequences.Length).Select(value => (long)value), sequences);

            var samples = await harness.ReadSamplesAsync(restarted.Id);
            AssertSampleSequences(samples, restarted.Settings.ThresholdConsecutiveCount);
            var report = Assert.Single(await harness.ReadTemperatureEventsAsync(restarted.Id));
            Assert.Equal(returnToNormal ? (byte)4 : (byte)3, report.EventType);
            var alarm = Assert.Single(harness.ReadTemperatureAlarms());
            Assert.Equal(originalAlarm.AlarmId, alarm.AlarmId);

            if (returnToNormal)
            {
                Assert.Equal((int?)originalAlarm.AlarmId, report.AlarmId);
                Assert.Equal((DateTime?)report.OccurredAtUtc, alarm.ClearedAtUtc);
                var firstNormalSamples = samples.Where(row => row.EquipmentId == 1)
                    .Take(restarted.Settings.ThresholdConsecutiveCount).ToArray();
                Assert.All(firstNormalSamples, sample => Assert.Equal((float?)23.456f, sample.Temperature));
                var lastRequired = firstNormalSamples.Last();
                // The sample and its report share a Modbus receipt time. Separate batches
                // can use different reference points, so allow less than one polling interval.
                var tolerance = TimeSpan.FromTicks(restarted.Settings.PollInterval.Ticks / 2);
                Assert.InRange(report.OccurredAtUtc - lastRequired.CollectedAtUtc, -tolerance, tolerance);
            }
            else
            {
                Assert.Null(alarm.ClearedAtUtc);
                Assert.Equal(originalAlarm.RaisedAtUtc, alarm.RaisedAtUtc);
            }
        }

        private static bool IsDeviceOneTemperature(AlarmEventItem item)
        {
            return item.EquipmentId == 1 && item.Cause == AlarmCause.Temperature;
        }

        private static async Task<SampleItem[]> WaitForOfflineSamplesAsync(CollectorRun run)
        {
            var samples = await PollAsync(_ => Task.FromResult(run.State.GetSampleSnapshot()),
                rows => Enumerable.Range(1, 5).All(id => rows.Count(row => row.EquipmentId == id) >= 3)
                    && rows.Last().ReceivedAt - rows.First().ReceivedAt >= TimeSpan.FromSeconds(1),
                rows => $"SAMPLE=0, ALARM_EVENT=0 (server not started), buffered samples={rows.Length}");
            Assert.True(run.State.SampleCount > 0);
            Assert.False(run.Timeline.HasReference);
            Assert.Equal(0L, run.State.SamplesDropped);
            return samples;
        }

        private static void AssertSampleSequences(SampleRow[] samples, int minimumPerEquipment)
        {
            Assert.Equal(Enumerable.Range(1, 5), samples.Select(row => row.EquipmentId).Distinct());
            foreach (var group in samples.GroupBy(row => row.EquipmentId))
            {
                var rows = group.ToArray();
                Assert.True(rows.Length >= minimumPerEquipment,
                    $"Equipment {group.Key} stored {rows.Length} samples.");
                for (int index = 0; index < rows.Length; index++)
                {
                    Assert.Equal(index + 1L, rows[index].SequenceNumber);
                    if (index > 0)
                        Assert.True(rows[index - 1].CollectedAtUtc <= rows[index].CollectedAtUtc,
                            $"Equipment {group.Key} UTC regressed at sequence {rows[index].SequenceNumber}.");
                }
            }
        }

        private static Task<SampleRow[]> WaitForSamplesAsync(Harness harness, CollectorRun run,
            Func<SampleRow[], bool> ready)
        {
            return PollAsync(token => harness.ReadSamplesAsync(run.Id, token), ready,
                rows => "SAMPLE=" + rows.Length + " (equipment 1–5: " + string.Join(", ",
                    Enumerable.Range(1, 5).Select(id => rows.Count(row => row.EquipmentId == id))) + ")");
        }

        private static Task<AlarmEventRow[]> WaitForTemperatureEventsAsync(Harness harness, CollectorRun run)
        {
            return PollAsync(token => harness.ReadTemperatureEventsAsync(run.Id, token), rows => rows.Length > 0,
                rows => $"ALARM_EVENT (equipment 1, temperature)={rows.Length}");
        }

        private static async Task<T> PollAsync<T>(Func<CancellationToken, Task<T>> read,
            Func<T, bool> ready, Func<T, string> counts)
        {
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                string lastCounts = "no completed read";
                try
                {
                    while (!deadline.IsCancellationRequested)
                    {
                        var latest = await read(deadline.Token);
                        lastCounts = counts(latest);
                        if (!deadline.IsCancellationRequested && ready(latest))
                            return latest;
                        await Task.Delay(100, deadline.Token);
                    }
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                }
                catch (SqlException) when (deadline.IsCancellationRequested)
                {
                }

                Assert.True(false, "Timed out waiting for Collector integration state. Last counts: " + lastCounts);
                return default(T);
            }
        }

        private static async Task WithHarnessAsync(Func<Harness, Task> scenario)
        {
            using (var harness = new Harness())
            {
                try
                {
                    await scenario(harness);
                }
                finally
                {
                    await harness.StopCollectorsAsync();
                }
                await harness.AssertCollectorsStoppedAsync();
            }
        }
    }
}
