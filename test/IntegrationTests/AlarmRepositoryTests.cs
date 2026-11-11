using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Dapper;
using Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace IntegrationTests
{
    public sealed class AlarmRepositoryTests : IDisposable
    {
        private readonly string databaseName = "MiniMES_AlarmTests_" + Guid.NewGuid().ToString("N");
        private readonly Guid runId = Guid.NewGuid();
        private readonly SqlConnection admin;
        private readonly SqlConnection connection;

        public AlarmRepositoryTests()
        {
            var configured = Environment.GetEnvironmentVariable("MINIMES_TEST_SQL_CONNECTION_STRING");
            Assert.False(string.IsNullOrWhiteSpace(configured),
                "Set MINIMES_TEST_SQL_CONNECTION_STRING to a test SQL Server connection string with CREATE DATABASE permission.");
            var builder = new SqlConnectionStringBuilder(configured)
            {
                InitialCatalog = "master",
                Pooling = false
            };
            admin = new SqlConnection(builder.ConnectionString);
            admin.Open();
            // Only this randomly named test database is created or removed.
            admin.Execute("CREATE DATABASE [" + databaseName + "]");
            builder.InitialCatalog = databaseName;
            connection = new SqlConnection(builder.ConnectionString);
            try
            {
                connection.Open();
                MigrationRunner.Run(builder.ConnectionString);
                new CollectorRunRepository(connection).EnsureRun(runId, DateTime.UtcNow);
                connection.Execute("SET IDENTITY_INSERT dbo.APP_USER ON; INSERT INTO dbo.APP_USER (UserId, Name) VALUES (101, N'First'), (202, N'Second'); SET IDENTITY_INSERT dbo.APP_USER OFF;");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        [Fact]
        public void AcknowledgeAlarm_Unacknowledged_ReturnsSuccessAndPersistsUserAndTime()
        {
            var alarmId = InsertAlarm();
            var nowUtc = new DateTime(2026, 11, 3, 1, 2, 3, DateTimeKind.Utc);

            var result = new AlarmRepository(connection).AcknowledgeAlarm(alarmId, 101, nowUtc);

            Assert.Equal(AlarmAcknowledgementStatus.Success, result.Status);
            Assert.Equal(101, result.AcknowledgedBy);
            AssertStoredAcknowledgement(alarmId, 101, nowUtc);
        }

        [Fact]
        public void AcknowledgeAlarm_AlreadyAcknowledged_ReturnsOriginalUserAndPreservesStoredValues()
        {
            var alarmId = InsertAlarm();
            var originalTime = new DateTime(2026, 11, 3, 1, 2, 3, DateTimeKind.Utc);
            connection.Execute(@"UPDATE dbo.ALARM
SET AcknowledgedAtUtc = @originalTime, AcknowledgedBy = @userId WHERE AlarmId = @alarmId",
                new { originalTime, userId = 101, alarmId });

            var result = new AlarmRepository(connection)
                .AcknowledgeAlarm(alarmId, 202, originalTime.AddMinutes(1));

            Assert.Equal(AlarmAcknowledgementStatus.AlreadyAcknowledged, result.Status);
            Assert.Equal(101, result.AcknowledgedBy);
            AssertStoredAcknowledgement(alarmId, 101, originalTime);
        }

        [Fact]
        public void AcknowledgeAlarm_MissingAlarm_ReturnsNotFound()
        {
            Assert.Equal(AlarmAcknowledgementStatus.NotFound,
                new AlarmRepository(connection).AcknowledgeAlarm(-1, 101, DateTime.UtcNow).Status);
        }

        [Fact]
        public void AcknowledgeAlarm_MissingUser_ThrowsAndPreservesAlarm()
        {
            var alarmId = InsertAlarm();
            Assert.Equal(547, Assert.Throws<SqlException>(() => new AlarmRepository(connection)
                .AcknowledgeAlarm(alarmId, -1, DateTime.UtcNow)).Number);
            Assert.Equal(1, connection.ExecuteScalar<int>(@"SELECT COUNT(*) FROM dbo.ALARM
WHERE AlarmId = @alarmId AND AcknowledgedAtUtc IS NULL AND AcknowledgedBy IS NULL", new { alarmId }));
        }

        [Fact]
        public void ActiveAlarm_UniqueIndex_RejectsSecondActiveAlarm()
        {
            InsertAlarm();
            var exception = Assert.Throws<SqlException>(() => InsertAlarm());
            Assert.Equal(2601, exception.Number);
            Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM"));
        }

        [Fact]
        public void ApplyEvents_RaiseAndReplay_CreatesOnlyOneActiveAlarm()
        {
            var item = NewEvent(1, 1);
            var repository = new AlarmRepository(connection);
            var first = repository.ApplyEvents(new[] { item });
            var replay = repository.ApplyEvents(new[] { item });

            Assert.Equal(1, first.AppliedCount);
            Assert.Equal(0, first.DuplicateCount);
            Assert.Empty(first.Mismatches);
            Assert.Equal(0, replay.AppliedCount);
            Assert.Equal(1, replay.DuplicateCount);
            Assert.Empty(replay.Mismatches);
            Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM_EVENT"));
            Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM WHERE ClearedAtUtc IS NULL"));
            Assert.Equal(item.OccurredAtUtc, connection.QuerySingle<DateTime>("SELECT RaisedAtUtc FROM dbo.ALARM"));
        }

        [Fact]
        public void ApplyEvents_MixedReplay_AppliesOnlyNewEvent()
        {
            var raised = NewEvent(1, 1);
            var cleared = NewEvent(2, 2);
            cleared.RunId = raised.RunId;
            var repository = new AlarmRepository(connection);
            repository.ApplyEvents(new[] { raised });
            var result = repository.ApplyEvents(new[] { raised, cleared });

            Assert.Equal(1, result.AppliedCount);
            Assert.Equal(1, result.DuplicateCount);
            Assert.Empty(result.Mismatches);
            Assert.Equal(2, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM_EVENT"));
            Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM"));
            Assert.Equal(cleared.OccurredAtUtc, connection.QuerySingle<DateTime?>("SELECT ClearedAtUtc FROM dbo.ALARM"));
        }

        [Fact]
        public void ApplyEvents_RaiseClearRaiseReplay_PreservesSecondActiveAlarm()
        {
            var events = new[] { NewEvent(1, 1), NewEvent(2, 2), NewEvent(3, 1) };
            foreach (var item in events)
                item.RunId = events[0].RunId;
            var repository = new AlarmRepository(connection);
            var first = repository.ApplyEvents(events);
            Assert.Equal(3, first.AppliedCount);
            Assert.Empty(first.Mismatches);
            AssertAlarmCycle(events);

            var replay = repository.ApplyEvents(events);
            Assert.Equal(0, replay.AppliedCount);
            Assert.Equal(3, replay.DuplicateCount);
            Assert.Empty(replay.Mismatches);
            AssertAlarmCycle(events);
            Assert.Equal(3, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM_EVENT"));
        }

        [Fact]
        public void ApplyEvents_DifferentRuns_PreservesReceivedOrder()
        {
            var raised = NewEvent(30, 1);
            raised.RunId = new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff");
            var cleared = NewEvent(1, 2);
            cleared.RunId = new Guid("00000000-0000-0000-0000-000000000001");
            new CollectorRunRepository(connection).EnsureRun(raised.RunId, DateTime.UtcNow);
            new CollectorRunRepository(connection).EnsureRun(cleared.RunId, DateTime.UtcNow);
            var result = new AlarmRepository(connection).ApplyEvents(new[] { raised, cleared });

            Assert.Equal(2, result.AppliedCount);
            Assert.Empty(result.Mismatches);
            Assert.Equal(raised.OccurredAtUtc, connection.QuerySingle<DateTime>("SELECT RaisedAtUtc FROM dbo.ALARM"));
            Assert.Equal(cleared.OccurredAtUtc, connection.QuerySingle<DateTime?>("SELECT ClearedAtUtc FROM dbo.ALARM"));
        }

        [Fact]
        public void ApplyEvents_RaiseWhileActive_RecordsMismatchWithoutChangingAlarm()
        {
            var first = NewEvent(1, 1);
            var repeated = NewEvent(2, 1);
            var repository = new AlarmRepository(connection);
            repository.ApplyEvents(new[] { first });
            var result = repository.ApplyEvents(new[] { repeated });

            AssertMismatch(result, repeated);
            Assert.Equal(2, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM_EVENT"));
            Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM"));
            Assert.Equal(first.OccurredAtUtc, connection.QuerySingle<DateTime>("SELECT RaisedAtUtc FROM dbo.ALARM"));
            Assert.Null(connection.QuerySingle<DateTime?>("SELECT ClearedAtUtc FROM dbo.ALARM"));
        }

        [Fact]
        public void ApplyEvents_ClearWithoutActiveAlarm_RecordsMismatch()
        {
            var item = NewEvent(1, 2);
            var result = new AlarmRepository(connection).ApplyEvents(new[] { item });

            AssertMismatch(result, item);
            Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM_EVENT"));
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM"));
        }

        [Fact]
        public void ApplyEvents_InvalidCauseInMiddle_RollsBackWholeBatch()
        {
            var invalid = NewEvent(2, 1);
            invalid.Cause = 9;
            var exception = Assert.Throws<SqlException>(() => new AlarmRepository(connection)
                .ApplyEvents(new[] { NewEvent(1, 1), invalid, NewEvent(3, 2) }));

            Assert.Equal(547, exception.Number);
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM_EVENT"));
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM"));
        }

        [Fact]
        public void ApplyEvents_UnsupportedTypeInBatch_RejectsBeforeAnyDatabaseChange()
        {
            Assert.Throws<ArgumentException>(() => new AlarmRepository(connection)
                .ApplyEvents(new[] { NewEvent(1, 1), NewEvent(2, 5) }));
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM_EVENT"));
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM"));
        }

        [Fact]
        public void ApplyEvents_UnsupportedTypeInCallerTransaction_LeavesEarlierWorkUntouched()
        {
            using (var transaction = connection.BeginTransaction())
            {
                var repository = new AlarmRepository(connection);
                repository.ApplyEvents(new[] { NewEvent(1, 1) }, transaction);
                Assert.Throws<ArgumentException>(() => repository.ApplyEvents(
                    new[] { NewEvent(2, 2), NewEvent(3, 255) }, transaction));
                Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM_EVENT", transaction: transaction));
                Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM WHERE ClearedAtUtc IS NULL", transaction: transaction));
                transaction.Rollback();
            }
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM_EVENT"));
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM"));
        }

        [Fact]
        public void ApplyEvents_CallerTransaction_CanRollBack()
        {
            using (var transaction = connection.BeginTransaction())
            {
                var result = new AlarmRepository(connection).ApplyEvents(new[] { NewEvent(1, 1) }, transaction);
                Assert.Equal(1, result.AppliedCount);
                Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM_EVENT", transaction: transaction));
                Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM", transaction: transaction));
                transaction.Rollback();
            }
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM_EVENT"));
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM"));
        }

        [Fact]
        public void ApplyEvents_ClosedConnection_RestoresClosedState()
        {
            connection.Close();
            var result = new AlarmRepository(connection).ApplyEvents(new[] { NewEvent(1, 1) });
            Assert.Equal(1, result.AppliedCount);
            Assert.Equal(ConnectionState.Closed, connection.State);
            connection.Open();
            Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM_EVENT"));
        }

        [Theory]
        [InlineData(1, false)]
        [InlineData(1, true)]
        [InlineData(2, false)]
        [InlineData(2, true)]
        [InlineData(3, false)]
        [InlineData(3, true)]
        [InlineData(4, false)]
        [InlineData(4, true)]
        public void ApplyEvents_StateMatrix_LinksOnlyChangedAlarm(byte eventType, bool active)
        {
            var originalTime = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
            int? originalId = null;
            if (active)
                originalId = connection.QuerySingle<int>(@"INSERT INTO dbo.ALARM
(EquipmentId, Cause, RaisedAtUtc) OUTPUT INSERTED.AlarmId VALUES (1, 2, @originalTime)", new { originalTime });
            var item = NewEvent(1, eventType);
            var result = new AlarmRepository(connection).ApplyEvents(new[] { item });
            var raises = eventType == 1 || eventType == 3;
            var changed = raises != active;
            Assert.Equal(1, result.AppliedCount);
            Assert.Equal(0, result.DuplicateCount);
            Assert.Equal((eventType == 1 && active) || (eventType == 2 && !active) ? 1 : 0,
                result.Mismatches.Count);
            var linkedId = connection.QuerySingle<int?>("SELECT AlarmId FROM dbo.ALARM_EVENT");
            Assert.Equal(changed, linkedId.HasValue);
            var rows = connection.Query<AlarmTimes>("SELECT RaisedAtUtc, ClearedAtUtc FROM dbo.ALARM").ToArray();
            Assert.Equal(active || raises ? 1 : 0, rows.Length);
            if (rows.Length == 1)
            {
                Assert.Equal(active ? originalTime : item.OccurredAtUtc, rows[0].RaisedAtUtc);
                Assert.Equal(active && !raises ? (DateTime?)item.OccurredAtUtc : null, rows[0].ClearedAtUtc);
                if (changed)
                    Assert.Equal(connection.QuerySingle<int>("SELECT AlarmId FROM dbo.ALARM"), linkedId);
                if (active && changed)
                    Assert.Equal(originalId, linkedId);
            }
            var replay = new AlarmRepository(connection).ApplyEvents(new[] { item });
            Assert.Equal(1, replay.DuplicateCount);
            Assert.Equal(linkedId, connection.QuerySingle<int?>("SELECT AlarmId FROM dbo.ALARM_EVENT"));
        }

        [Fact]
        public void ApplyEvents_MixedReports_EachAlarmHasOnlyItsOpeningAndClosingEvents()
        {
            var types = new byte[] { 1, 1, 3, 4, 4, 3, 3, 2, 2, 3 };
            var events = types.Select((type, index) => NewEvent(index + 1, type)).ToArray();
            new AlarmRepository(connection).ApplyEvents(events);
            var alarms = connection.Query<int>("SELECT AlarmId FROM dbo.ALARM ORDER BY AlarmId").ToArray();
            Assert.Equal(3, alarms.Length);
            var expected = new[] { new long[] { 1, 4 }, new long[] { 6, 8 }, new long[] { 10 } };
            for (var index = 0; index < alarms.Length; index++)
            {
                var id = alarms[index];
                Assert.Equal(expected[index], connection.Query<long>(@"SELECT EventSequence FROM dbo.ALARM_EVENT
WHERE AlarmId = @id ORDER BY EventSequence", new { id }).ToArray());
                var times = connection.QuerySingle<AlarmTimes>("SELECT RaisedAtUtc, ClearedAtUtc FROM dbo.ALARM WHERE AlarmId = @id", new { id });
                Assert.Equal(events[expected[index][0] - 1].OccurredAtUtc, times.RaisedAtUtc);
                Assert.Equal(expected[index].Length == 2 ? (DateTime?)events[expected[index][1] - 1].OccurredAtUtc : null, times.ClearedAtUtc);
            }
        }

        [Fact]
        public void ApplyEvents_UnregisteredRun_RollsBackEventsAndAlarms()
        {
            var invalid = NewEvent(2, 4);
            invalid.RunId = Guid.NewGuid();
            var error = Assert.Throws<SqlException>(() => new AlarmRepository(connection)
                .ApplyEvents(new[] { NewEvent(1, 1), invalid }));
            Assert.Equal(547, error.Number);
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM_EVENT"));
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ALARM"));
        }

        private AlarmEventRecord NewEvent(long sequence, byte eventType)
        {
            return new AlarmEventRecord
            {
                RunId = runId, EventSequence = sequence, EquipmentId = 1,
                Cause = 2, EventType = eventType,
                OccurredAtUtc = new DateTime(2026, 11, 6, 1, 2, 3, 123, DateTimeKind.Utc).AddSeconds(sequence)
            };
        }

        private static void AssertMismatch(AlarmEventBatchResult result, AlarmEventRecord item)
        {
            Assert.Equal(1, result.AppliedCount);
            Assert.Equal(0, result.DuplicateCount);
            var mismatch = Assert.Single(result.Mismatches);
            Assert.Equal(item.RunId, mismatch.RunId);
            Assert.Equal(item.EventSequence, mismatch.EventSequence);
            Assert.Equal(item.EquipmentId, mismatch.EquipmentId);
            Assert.Equal(item.Cause, mismatch.Cause);
            Assert.Equal(item.EventType, mismatch.EventType);
        }

        private void AssertAlarmCycle(AlarmEventRecord[] events)
        {
            var rows = connection.Query<AlarmTimes>(
                "SELECT RaisedAtUtc, ClearedAtUtc FROM dbo.ALARM ORDER BY AlarmId").ToArray();
            Assert.Equal(2, rows.Length);
            Assert.Equal(events[0].OccurredAtUtc, rows[0].RaisedAtUtc);
            Assert.Equal(events[1].OccurredAtUtc, rows[0].ClearedAtUtc);
            Assert.Equal(events[2].OccurredAtUtc, rows[1].RaisedAtUtc);
            Assert.Null(rows[1].ClearedAtUtc);
        }

        private sealed class AlarmTimes
        {
            public DateTime RaisedAtUtc { get; set; }
            public DateTime? ClearedAtUtc { get; set; }
        }


        private int InsertAlarm()
        {
            return connection.QuerySingle<int>(@"INSERT INTO dbo.ALARM (EquipmentId, Cause, RaisedAtUtc)
OUTPUT INSERTED.AlarmId VALUES (@equipmentId, @cause, @raisedAtUtc)",
                new { equipmentId = 1, cause = (byte)2, raisedAtUtc = DateTime.UtcNow });
        }

        private void AssertStoredAcknowledgement(int alarmId, int userId, DateTime time)
        {
            using (var command = new SqlCommand(
                "SELECT AcknowledgedAtUtc, AcknowledgedBy FROM dbo.ALARM WHERE AlarmId = @alarmId", connection))
            {
                command.Parameters.AddWithValue("@alarmId", alarmId);
                using (var reader = command.ExecuteReader())
                {
                    Assert.True(reader.Read());
                    Assert.Equal(time, reader.GetDateTime(0));
                    Assert.Equal(userId, reader.GetInt32(1));
                    Assert.False(reader.Read());
                }
            }
        }

        public void Dispose()
        {
            connection.Dispose();
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
