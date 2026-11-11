using System;
using System.Collections.Generic;
using System.IO;
using Dapper;
using Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace IntegrationTests
{
    public sealed class FaultRepositoryTests : IDisposable
    {
        private readonly string databaseName = "MiniMES_FaultTests_" + Guid.NewGuid().ToString("N");
        private readonly SqlConnection admin;
        private readonly SqlConnection connection;
        private readonly int userId;
        private readonly int faultId;
        private readonly int alarmId;
        private readonly DateTime nowUtc = new DateTime(2026, 11, 3, 1, 2, 3, DateTimeKind.Utc);

        public FaultRepositoryTests()
        {
            var configured = Environment.GetEnvironmentVariable("MINIMES_TEST_SQL_CONNECTION_STRING");
            Assert.False(string.IsNullOrWhiteSpace(configured),
                "Set MINIMES_TEST_SQL_CONNECTION_STRING to a test SQL Server connection string with CREATE DATABASE permission.");
            var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master", Pooling = false };
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
                userId = connection.QuerySingle<int>(
                    "INSERT INTO dbo.APP_USER (Name) OUTPUT INSERTED.UserId VALUES (N'Test User')");
                alarmId = connection.QuerySingle<int>(@"INSERT INTO dbo.ALARM (EquipmentId, Cause, RaisedAtUtc)
OUTPUT INSERTED.AlarmId VALUES (1, 1, @nowUtc)", new { nowUtc });
                faultId = connection.QuerySingle<int>(@"INSERT INTO dbo.FAULT (AlarmId, Status, RegisteredAtUtc, CauseCategory)
OUTPUT INSERTED.FaultId VALUES (@alarmId, 1, @nowUtc, 1)", new { alarmId, nowUtc });
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        [Theory]
        [InlineData(1, 2)]
        [InlineData(2, 3)]
        [InlineData(1, 4)]
        public void TransitionFault_MatchingStatus_CommitsStatusAndHistory(byte previous, byte next)
        {
            connection.Execute("UPDATE dbo.FAULT SET Status = @previous WHERE FaultId = @faultId", new { previous, faultId });
            const string reason = "조치 사유: '점검'; -- parameter binding";

            var result = new FaultRepository(connection).TransitionFault(faultId, previous, next, userId, nowUtc, reason, next == 3 ? (DateTime?)nowUtc.AddMinutes(-1) : null);

            Assert.Equal(FaultTransitionStatus.Success, result.Status);
            Assert.Equal(next, result.CurrentStatus);
            Assert.Equal(next, ReadStatus());
            var history = connection.QuerySingle<HistoryRow>("SELECT * FROM dbo.FAULT_HISTORY WHERE FaultId = @faultId", new { faultId });
            Assert.Equal(faultId, history.FaultId);
            Assert.Equal(userId, history.ChangedByUserId);
            Assert.Equal(nowUtc, history.ChangedAtUtc);
            Assert.Equal(previous, history.PreviousStatus);
            Assert.Equal(next, history.NewStatus);
            Assert.Equal(reason, history.Reason);
        }

        [Fact]
        public void TransitionFault_StaleExpectedStatus_ReturnsCurrentStatusWithoutAddingHistory()
        {
            var repository = new FaultRepository(connection);
            repository.TransitionFault(faultId, 1, 2, userId, nowUtc, null, null);

            var result = repository.TransitionFault(faultId, 1, 4, userId, nowUtc.AddMinutes(1), "stale request", null);

            Assert.Equal(FaultTransitionStatus.StatusMismatch, result.Status);
            Assert.Equal((byte)2, result.CurrentStatus);
            Assert.Equal((byte)2, ReadStatus());
            var history = connection.QuerySingle<HistoryRow>("SELECT * FROM dbo.FAULT_HISTORY");
            Assert.Equal((byte)1, history.PreviousStatus);
            Assert.Equal((byte)2, history.NewStatus);
            Assert.Equal(nowUtc, history.ChangedAtUtc);
            Assert.Null(history.Reason);
        }

        [Fact]
        public void TransitionFault_HistoryInsertFails_RollsBackStatus()
        {
            var error = Assert.Throws<SqlException>(() => new FaultRepository(connection)
                .TransitionFault(faultId, 1, 2, -1, nowUtc, "invalid user", null));

            Assert.Equal(547, error.Number);
            Assert.Equal((byte)1, ReadStatus());
            Assert.Equal(0, connection.QuerySingle<int>("SELECT COUNT(*) FROM dbo.FAULT_HISTORY"));
        }

        [Fact]
        public void TransitionFault_MissingFault_ReturnsNotFoundWithoutAddingHistory()
        {
            Assert.Equal(FaultTransitionStatus.NotFound, new FaultRepository(connection)
                .TransitionFault(-1, 1, 2, userId, nowUtc, null, null).Status);
            Assert.Equal(0, connection.QuerySingle<int>("SELECT COUNT(*) FROM dbo.FAULT_HISTORY"));
        }

        [Theory]
        [InlineData("조치: '부품 교체'; -- bound text")]
        [InlineData(null)]
        public void UpdateTexts_MatchingVersion_ReturnsNewVersionUsableForNextUpdate(string text)
        {
            var original = ReadActionText();
            var repository = new FaultRepository(connection);

            var result = repository.UpdateTexts(faultId, original.Version, 2, "cause", text);

            Assert.Equal(FaultTextsUpdateStatus.Success, result.Status);
            Assert.Equal(text, result.ActionText);
            Assert.Equal(8, result.Version.Length);
            Assert.NotEqual(original.Version, result.Version);
            var stored = ReadActionText();
            Assert.Equal(text, stored.ActionText);
            Assert.Equal((byte)2, stored.CauseCategory);
            Assert.Equal("cause", stored.CauseText);
            Assert.Equal(result.Version, stored.Version);

            var next = repository.UpdateTexts(faultId, result.Version, 2, "cause", "next edit");
            Assert.Equal(FaultTextsUpdateStatus.Success, next.Status);
            Assert.NotEqual(result.Version, next.Version);
            Assert.Equal(next.Version, ReadActionText().Version);
            Assert.Equal("next edit", ReadActionText().ActionText);
        }

        [Theory]
        [InlineData("first user's text")]
        [InlineData(null)]
        public void UpdateTexts_StaleVersion_ReturnsCurrentTextAndVersionWithoutOverwriting(string currentText)
        {
            var original = ReadActionText();
            var repository = new FaultRepository(connection);
            var first = repository.UpdateTexts(faultId, original.Version, 2, "cause", currentText);

            var conflict = repository.UpdateTexts(faultId, original.Version, 3, "stale cause", "stale edit");

            Assert.Equal(FaultTextsUpdateStatus.Conflict, conflict.Status);
            Assert.Equal(currentText, conflict.ActionText);
            Assert.Equal((byte)2, conflict.CauseCategory);
            Assert.Equal("cause", conflict.CauseText);
            Assert.Equal(first.Version, conflict.Version);
            var stored = ReadActionText();
            Assert.Equal(currentText, stored.ActionText);
            Assert.Equal((byte)2, stored.CauseCategory);
            Assert.Equal("cause", stored.CauseText);
            Assert.Equal(first.Version, stored.Version);

            var retry = repository.UpdateTexts(faultId, conflict.Version, 2, "cause", "resolved edit");
            Assert.Equal(FaultTextsUpdateStatus.Success, retry.Status);
            Assert.Equal("resolved edit", ReadActionText().ActionText);
        }

        [Fact]
        public void UpdateTexts_MissingFault_ReturnsNotFound()
        {
            Assert.Equal(FaultTextsUpdateStatus.NotFound, new FaultRepository(connection)
                .UpdateTexts(-1, ReadActionText().Version, 2, "cause", "edit").Status);
        }

        [Fact]
        public void UpdateTexts_MissingFaultAndInvalidCategory_ReturnsNotFound()
        {
            Assert.Equal(FaultTextsUpdateStatus.NotFound, new FaultRepository(connection)
                .UpdateTexts(-1, ReadActionText().Version, 7, "cause", "edit").Status);
        }

        [Fact]
        public void RegisterFault_StoresValuesAndReturnsExistingIdUntilCancelled()
        {
            var repository = new FaultRepository(connection);
            Assert.Equal(FaultRegisterStatus.AlarmNotFound,
                repository.RegisterFault(-1, 7, null, null, nowUtc).Status);
            Assert.Equal(FaultRegisterStatus.InvalidCauseCategory,
                repository.RegisterFault(alarmId, 7, null, null, nowUtc).Status);
            var duplicate = repository.RegisterFault(alarmId, 1, null, null, nowUtc);
            Assert.Equal(FaultRegisterStatus.AlreadyRegistered, duplicate.Status);
            Assert.Equal(faultId, duplicate.FaultId);
            Assert.Equal(FaultTransitionStatus.Success,
                repository.TransitionFault(faultId, 1, 4, userId, nowUtc, "cancel", null).Status);
            var registeredAt = nowUtc.AddMilliseconds(123);
            var result = repository.RegisterFault(alarmId, 2, "원인 ' --", "조치 ' --", registeredAt);
            Assert.Equal(FaultRegisterStatus.Success, result.Status);
            Assert.NotEqual(faultId, result.FaultId);
            Assert.Equal(8, result.Version.Length);
            var stored = connection.QuerySingle<RegisteredRow>("SELECT * FROM dbo.FAULT WHERE FaultId = @id", new { id = result.FaultId });
            Assert.Equal((byte)1, stored.Status);
            Assert.Equal(registeredAt, stored.RegisteredAtUtc);
            Assert.Equal((byte)2, stored.CauseCategory);
            Assert.Equal("원인 ' --", stored.CauseText);
            Assert.Equal("조치 ' --", stored.ActionText);
            Assert.Equal(result.Version, stored.Version);
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.FAULT_HISTORY WHERE FaultId = @id", new { id = result.FaultId }));
        }

        [Fact]
        public void TransitionFault_CloseReopenClose_StoresAndClearsBothTimes()
        {
            var repository = new FaultRepository(connection);
            Assert.Equal(FaultTransitionStatus.Success, repository.TransitionFault(faultId, 1, 3, userId, nowUtc, null, nowUtc.AddMinutes(-1)).Status);
            AssertTimes(nowUtc.AddMinutes(-1), nowUtc);
            Assert.Equal(FaultTransitionStatus.Success, repository.TransitionFault(faultId, 3, 2, userId, nowUtc.AddMinutes(1), "reopen", null).Status);
            AssertTimes(null, null);
            Assert.Equal(FaultTransitionStatus.Success, repository.TransitionFault(faultId, 2, 3, userId, nowUtc.AddMinutes(3), null, nowUtc.AddMinutes(2)).Status);
            AssertTimes(nowUtc.AddMinutes(2), nowUtc.AddMinutes(3));
            Assert.Equal(3, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.FAULT_HISTORY WHERE FaultId = @faultId", new { faultId }));
        }

        [Theory]
        [InlineData(3, false)]
        [InlineData(2, true)]
        public void TransitionFault_InvalidRecoveryPresence_RejectsBeforeWriting(byte next, bool hasRecovery)
        {
            Assert.Throws<ArgumentException>(() => new FaultRepository(connection).TransitionFault(
                faultId, 1, next, userId, nowUtc, null, hasRecovery ? (DateTime?)nowUtc : null));
            Assert.Equal((byte)1, ReadStatus());
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.FAULT_HISTORY"));
        }

        [Fact]
        public void TransitionFault_RestoreConflict_ReturnsActiveIdWithoutHistoryThenAllowsRestore()
        {
            var repository = new FaultRepository(connection);
            repository.TransitionFault(faultId, 1, 4, userId, nowUtc, "cancel", null);
            var other = repository.RegisterFault(alarmId, 1, null, null, nowUtc);
            Assert.Equal(FaultRegisterStatus.Success, other.Status);
            var result = repository.TransitionFault(faultId, 4, 1, userId, nowUtc, "restore", null);
            Assert.Equal(FaultTransitionStatus.ActiveFaultExists, result.Status);
            Assert.Equal(other.FaultId, result.FaultId);
            Assert.Equal((byte)4, ReadStatus());
            Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.FAULT_HISTORY"));
            repository.TransitionFault(other.FaultId.Value, 1, 4, userId, nowUtc, "cancel other", null);
            Assert.Equal(FaultTransitionStatus.Success,
                repository.TransitionFault(faultId, 4, 1, userId, nowUtc, "restore", null).Status);
            Assert.Equal((byte)1, ReadStatus());
            Assert.Equal(3, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.FAULT_HISTORY"));
        }

        [Fact]
        public void UpdateTexts_ClosedFault_RejectsEvenCurrentVersionAndAllowsAfterReopen()
        {
            var repository = new FaultRepository(connection);
            repository.UpdateTexts(faultId, ReadActionText().Version, 2, "before cause", "before action");
            repository.TransitionFault(faultId, 1, 3, userId, nowUtc, null, nowUtc);
            var before = ReadActionText();
            var result = repository.UpdateTexts(faultId, before.Version, 3, "after cause", "after action");
            Assert.Equal(FaultTextsUpdateStatus.FaultClosed, result.Status);
            Assert.Equal((byte)3, result.CurrentStatus);
            var after = ReadActionText();
            Assert.Equal(before.Version, after.Version);
            Assert.Equal(before.CauseCategory, after.CauseCategory);
            Assert.Equal(before.CauseText, after.CauseText);
            Assert.Equal(before.ActionText, after.ActionText);
            repository.TransitionFault(faultId, 3, 2, userId, nowUtc, "reopen", null);
            Assert.Equal(FaultTextsUpdateStatus.Success, repository.UpdateTexts(faultId,
                ReadActionText().Version, 3, "after cause", "after action").Status);
        }

        [Fact]
        public void UpdateTexts_InvalidCategory_PreservesAllValues()
        {
            var repository = new FaultRepository(connection);
            repository.UpdateTexts(faultId, ReadActionText().Version, 2, "cause", "action");
            var before = ReadActionText();
            Assert.Equal(FaultTextsUpdateStatus.InvalidCauseCategory,
                repository.UpdateTexts(faultId, before.Version, 7, "changed", "changed").Status);
            var after = ReadActionText();
            Assert.Equal(before.Version, after.Version);
            Assert.Equal(before.CauseCategory, after.CauseCategory);
            Assert.Equal(before.CauseText, after.CauseText);
            Assert.Equal(before.ActionText, after.ActionText);
        }

        [Fact]
        public void UpdateTexts_TransitionAfterRead_ReturnsConflictAndAllowsCancelledEdit()
        {
            var repository = new FaultRepository(connection);
            repository.UpdateTexts(faultId, ReadActionText().Version, 2, "cause", "action");
            var before = ReadActionText();
            repository.TransitionFault(faultId, 1, 4, userId, nowUtc, "cancel", null);
            var current = ReadActionText();
            var result = repository.UpdateTexts(faultId, before.Version, 3, "changed", "changed");
            Assert.Equal(FaultTextsUpdateStatus.Conflict, result.Status);
            Assert.Equal(current.Version, result.Version);
            Assert.NotEqual(before.Version, result.Version);
            Assert.Equal(current.CauseCategory, result.CauseCategory);
            Assert.Equal(current.CauseText, result.CauseText);
            Assert.Equal(current.ActionText, result.ActionText);
            Assert.Equal(FaultTextsUpdateStatus.Success,
                repository.UpdateTexts(faultId, result.Version, 3, null, null).Status);
        }

        [Fact]
        public void UpdateTexts_InvalidVersion_RejectsBeforeWriting()
        {
            var repository = new FaultRepository(connection);
            Assert.Throws<ArgumentNullException>(() => repository.UpdateTexts(faultId, null, 1, null, null));
            Assert.Throws<ArgumentException>(() => repository.UpdateTexts(faultId, new byte[7], 1, null, null));
        }

        private void AssertTimes(DateTime? recovered, DateTime? closed)
        {
            var stored = connection.QuerySingle<FaultTimes>("SELECT RecoveredAtUtc, ClosedAtUtc FROM dbo.FAULT WHERE FaultId = @faultId", new { faultId });
            Assert.Equal(recovered, stored.RecoveredAtUtc);
            Assert.Equal(closed, stored.ClosedAtUtc);
        }

        private sealed class FaultTimes
        {
            public DateTime? RecoveredAtUtc { get; set; }
            public DateTime? ClosedAtUtc { get; set; }
        }

        private sealed class RegisteredRow
        {
            public byte Status { get; set; }
            public DateTime RegisteredAtUtc { get; set; }
            public byte CauseCategory { get; set; }
            public string CauseText { get; set; }
            public string ActionText { get; set; }
            public byte[] Version { get; set; }
        }

        private ActionTextRow ReadActionText()
        {
            return connection.QuerySingle<ActionTextRow>(
                "SELECT CauseCategory, CauseText, ActionText, Version FROM dbo.FAULT WHERE FaultId = @faultId", new { faultId });
        }

        private sealed class ActionTextRow
        {
            public byte CauseCategory { get; set; }
            public string CauseText { get; set; }
            public string ActionText { get; set; }
            public byte[] Version { get; set; }
        }

        private byte ReadStatus()
        {
            return connection.QuerySingle<byte>("SELECT Status FROM dbo.FAULT WHERE FaultId = @faultId", new { faultId });
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
