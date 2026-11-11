using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Data;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace IntegrationTests
{
    public sealed class RepositoryConcurrencyTests : IDisposable
    {
        private const int Repetitions = 100;
        private readonly string databaseName = "MiniMES_ConcurrencyTests_" + Guid.NewGuid().ToString("N");
        private readonly SqlConnection admin;
        private readonly SqlConnection setup;
        private readonly SqlConnection[] workers = new SqlConnection[2];
        private readonly int[] userIds;
        private readonly int alarmId;
        private readonly int faultId;
        private readonly ITestOutputHelper output;
        private readonly DateTime[] times =
        {
            new DateTime(2026, 11, 5, 1, 2, 1, DateTimeKind.Utc),
            new DateTime(2026, 11, 5, 1, 2, 2, DateTimeKind.Utc)
        };

        public RepositoryConcurrencyTests(ITestOutputHelper output)
        {
            this.output = output;
            var configured = Environment.GetEnvironmentVariable("MINIMES_TEST_SQL_CONNECTION_STRING");
            Assert.False(string.IsNullOrWhiteSpace(configured),
                "Set MINIMES_TEST_SQL_CONNECTION_STRING to a test SQL Server connection string with CREATE DATABASE permission.");
            var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master", Pooling = false };
            admin = new SqlConnection(builder.ConnectionString);
            admin.Open();
            // Only this randomly named test database is created or removed.
            admin.Execute("CREATE DATABASE [" + databaseName + "]");
            builder.InitialCatalog = databaseName;
            setup = new SqlConnection(builder.ConnectionString);
            try
            {
                setup.Open();
                MigrationRunner.Run(builder.ConnectionString);

                userIds = setup.Query<int>(@"INSERT INTO dbo.APP_USER (Name)
OUTPUT INSERTED.UserId VALUES (N'작업자 1'), (N'작업자 2')").ToArray();
                alarmId = setup.QuerySingle<int>(@"INSERT INTO dbo.ALARM (EquipmentId, Cause, RaisedAtUtc)
OUTPUT INSERTED.AlarmId VALUES (1, 1, @now)", new { now = times[0] });
                faultId = setup.QuerySingle<int>(@"INSERT INTO dbo.FAULT (AlarmId, Status, RegisteredAtUtc, CauseCategory)
OUTPUT INSERTED.FaultId VALUES (@alarmId, 1, @now, 1)", new { alarmId, now = times[0] });
                // Each worker reuses its own connection across all repetitions without sharing it.
                for (var i = 0; i < workers.Length; i++)
                {
                    workers[i] = new SqlConnection(builder.ConnectionString);
                    workers[i].Open();
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        [Fact]
        public void AcknowledgeAlarm_ConcurrentRequests_ExactlyOneWins()
        {
            var repositories = workers.Select(connection => new AlarmRepository(connection)).ToArray();
            Repeat(iteration =>
            {
                // Repeat contention on the same row while preserving the active alarm uniqueness constraint.
                setup.Execute(@"UPDATE dbo.ALARM SET AcknowledgedAtUtc = NULL, AcknowledgedBy = NULL
WHERE AlarmId = @alarmId", new { alarmId });
                var results = RunPair(i => repositories[i].AcknowledgeAlarm(alarmId, userIds[i], times[i]));

                Assert.Single(results, result => result.Status == AlarmAcknowledgementStatus.Success);
                var loser = Assert.Single(results, result => result.Status == AlarmAcknowledgementStatus.AlreadyAcknowledged);
                var winner = Array.FindIndex(results, result => result.Status == AlarmAcknowledgementStatus.Success);
                Assert.Equal(userIds[winner], loser.AcknowledgedBy);
                var stored = setup.QuerySingle<AlarmRow>(
                    "SELECT AcknowledgedBy, AcknowledgedAtUtc FROM dbo.ALARM WHERE AlarmId = @alarmId", new { alarmId });
                Assert.Equal(userIds[winner], stored.AcknowledgedBy);
                Assert.Equal(times[winner], stored.AcknowledgedAtUtc);
            });
        }

        [Fact]
        public void TransitionFault_ConcurrentRequests_ExactlyOneStatusChangeAndHistoryRow()
        {
            var repositories = workers.Select(connection => new FaultRepository(connection)).ToArray();
            Repeat(iteration =>
            {
                setup.Execute(@"DELETE FROM dbo.FAULT_HISTORY WHERE FaultId = @faultId;
UPDATE dbo.FAULT SET Status = 1 WHERE FaultId = @faultId", new { faultId });
                var reasons = new[] { "작업자 1 전환 " + iteration, "작업자 2 전환 " + iteration };
                var results = RunPair(i => repositories[i].TransitionFault(faultId, 1, 2, userIds[i], times[i], reasons[i]));

                Assert.Single(results, result => result.Status == FaultTransitionStatus.Success);
                var loser = Assert.Single(results, result => result.Status == FaultTransitionStatus.StatusMismatch);
                Assert.Equal((byte)2, loser.CurrentStatus);
                var winner = Array.FindIndex(results, result => result.Status == FaultTransitionStatus.Success);
                Assert.Equal((byte)2, setup.QuerySingle<byte>(
                    "SELECT Status FROM dbo.FAULT WHERE FaultId = @faultId", new { faultId }));
                var history = Assert.Single(setup.Query<HistoryRow>(
                    "SELECT * FROM dbo.FAULT_HISTORY WHERE FaultId = @faultId", new { faultId }));
                Assert.Equal(faultId, history.FaultId);
                Assert.Equal(userIds[winner], history.ChangedByUserId);
                Assert.Equal(times[winner], history.ChangedAtUtc);
                Assert.Equal((byte)1, history.PreviousStatus);
                Assert.Equal((byte)2, history.NewStatus);
                Assert.Equal(reasons[winner], history.Reason);
            });
        }

        [Fact]
        public void UpdateActionText_ConcurrentRequests_ExactlyOneWinsAndConflictReturnsWinner()
        {
            var repositories = workers.Select(connection => new FaultRepository(connection)).ToArray();
            Repeat(iteration =>
            {
                var version = setup.QuerySingle<byte[]>("SELECT Version FROM dbo.FAULT WHERE FaultId = @faultId", new { faultId });
                var texts = new[] { "작업자 1 수정 " + iteration, "작업자 2 수정 " + iteration };
                var results = RunPair(i => repositories[i].UpdateActionText(faultId, version, texts[i]));

                var success = Assert.Single(results, result => result.Status == FaultActionTextUpdateStatus.Success);
                var conflict = Assert.Single(results, result => result.Status == FaultActionTextUpdateStatus.Conflict);
                var winner = Array.FindIndex(results, result => result.Status == FaultActionTextUpdateStatus.Success);
                Assert.Equal(texts[winner], conflict.ActionText);
                Assert.Equal(success.Version, conflict.Version);
                Assert.NotEqual(version, success.Version);
                var stored = setup.QuerySingle<ActionTextRow>(
                    "SELECT ActionText, Version FROM dbo.FAULT WHERE FaultId = @faultId", new { faultId });
                Assert.Equal(texts[winner], stored.ActionText);
                Assert.Equal(success.Version, stored.Version);
            });
        }

        [Fact]
        public void EnsureRun_ConcurrentRequests_PreservesSingleFirstRegistration()
        {
            var repositories = workers.Select(connection => new CollectorRunRepository(connection)).ToArray();
            Repeat(iteration =>
            {
                var runId = Guid.NewGuid();
                var results = RunPair(i =>
                {
                    repositories[i].EnsureRun(runId, times[i]);
                    return true;
                });
                Assert.All(results, result => Assert.True(result));
                Assert.Equal(1, setup.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.COLLECTOR_RUN WHERE RunId = @runId", new { runId }));
                var stored = setup.QuerySingle<DateTime>("SELECT StartedAtUtc FROM dbo.COLLECTOR_RUN WHERE RunId = @runId", new { runId });
                Assert.Contains(stored, times);
            });
        }

        private void Repeat(Action<int> check)
        {
            for (var iteration = 1; iteration <= Repetitions; iteration++)
            {
                try
                {
                    check(iteration);
                }
                catch (Exception error)
                {
                    throw new XunitException("Iteration " + iteration + "/" + Repetitions + " failed: " + error);
                }
            }
            output.WriteLine("All {0} concurrent request repetitions passed. No retries.", Repetitions);
        }

        private static T[] RunPair<T>(Func<int, T> request)
        {
            using (var barrier = new Barrier(2))
            {
                var tasks = Enumerable.Range(0, 2).Select(worker => Task.Factory.StartNew(() =>
                {
                    // Avoid thread pool startup delays and do not wait indefinitely if either worker fails.
                    if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Timed out waiting for concurrent start: worker " + worker);
                    return request(worker);
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

                // Wait for both tasks to finish and propagate their exceptions as an AggregateException.
                Task.WaitAll(tasks);
                return tasks.Select(task => task.Result).ToArray();
            }
        }

        private sealed class AlarmRow
        {
            public int? AcknowledgedBy { get; set; }
            public DateTime? AcknowledgedAtUtc { get; set; }
        }

        private sealed class HistoryRow
        {
            public int FaultId { get; set; }
            public int ChangedByUserId { get; set; }
            public DateTime ChangedAtUtc { get; set; }
            public byte PreviousStatus { get; set; }
            public byte NewStatus { get; set; }
            public string Reason { get; set; }
        }

        private sealed class ActionTextRow
        {
            public string ActionText { get; set; }
            public byte[] Version { get; set; }
        }

        public void Dispose()
        {
            foreach (var worker in workers)
                worker?.Dispose();
            setup?.Dispose();
            try
            {
                admin.Execute("ALTER DATABASE [" + databaseName + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
                admin.Execute("DROP DATABASE [" + databaseName + "]");
            }
            finally
            {
                admin.Dispose();
            }
        }
    }
}
