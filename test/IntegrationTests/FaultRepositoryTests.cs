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
                foreach (var script in new[] { "0002_CreateAlarm.sql", "0004_CreateFault.sql", "0005_CreateAppUser.sql" })
                {
                    using (var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream("Data.Migrations." + script))
                    using (var reader = new StreamReader(stream))
                        connection.Execute(reader.ReadToEnd());
                }
                userId = connection.QuerySingle<int>(
                    "INSERT INTO dbo.APP_USER (Name) OUTPUT INSERTED.UserId VALUES (N'Test User')");
                var alarmId = connection.QuerySingle<int>(@"INSERT INTO dbo.ALARM (EquipmentId, Cause, RaisedAtUtc)
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

            var result = new FaultRepository(connection).TransitionFault(faultId, previous, next, userId, nowUtc, reason);

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
            repository.TransitionFault(faultId, 1, 2, userId, nowUtc, null);

            var result = repository.TransitionFault(faultId, 1, 4, userId, nowUtc.AddMinutes(1), "stale request");

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
                .TransitionFault(faultId, 1, 2, -1, nowUtc, "invalid user"));

            Assert.Equal(547, error.Number);
            Assert.Equal((byte)1, ReadStatus());
            Assert.Equal(0, connection.QuerySingle<int>("SELECT COUNT(*) FROM dbo.FAULT_HISTORY"));
        }

        [Fact]
        public void TransitionFault_MissingFault_ThrowsWithoutAddingHistory()
        {
            Assert.Throws<KeyNotFoundException>(() => new FaultRepository(connection)
                .TransitionFault(-1, 1, 2, userId, nowUtc, null));
            Assert.Equal(0, connection.QuerySingle<int>("SELECT COUNT(*) FROM dbo.FAULT_HISTORY"));
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
