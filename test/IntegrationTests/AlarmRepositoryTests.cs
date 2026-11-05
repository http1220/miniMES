using System;
using System.Collections.Generic;
using System.IO;
using Dapper;
using Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace IntegrationTests
{
    public sealed class AlarmRepositoryTests : IDisposable
    {
        private readonly string databaseName = "MiniMES_AlarmTests_" + Guid.NewGuid().ToString("N");
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
                using (var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream(
                    "Data.Migrations.0002_CreateAlarm.sql"))
                using (var reader = new StreamReader(stream))
                {
                    connection.Execute(reader.ReadToEnd());
                }
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
        public void AcknowledgeAlarm_MissingAlarm_ThrowsKeyNotFoundException()
        {
            Assert.Throws<KeyNotFoundException>(() =>
                new AlarmRepository(connection).AcknowledgeAlarm(-1, 101, DateTime.UtcNow));
        }

        private int InsertAlarm()
        {
            return connection.QuerySingle<int>(@"INSERT INTO dbo.ALARM (EquipmentId, Cause, RaisedAtUtc)
OUTPUT INSERTED.AlarmId VALUES (1, 2, SYSUTCDATETIME())");
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
