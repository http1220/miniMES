using System;
using System.Collections.Generic;
using System.IO;
using Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace IntegrationTests
{
    public class MigrationRunnerTests
    {
        [Theory]
        [InlineData("Data.Migrations.0001_CreateSample.sql", "CREATE TABLE SAMPLE")]
        [InlineData("Data.Migrations.0002_CreateAlarm.sql", "CREATE TABLE dbo.ALARM")]
        [InlineData("Data.Migrations.0003_CreateAlarmEvent.sql", "CREATE TABLE dbo.ALARM_EVENT")]
        [InlineData("Data.Migrations.0004_CreateFault.sql", "CREATE TABLE dbo.FAULT (")]
        [InlineData("Data.Migrations.0004_CreateFault.sql", "CREATE TABLE dbo.FAULT_HISTORY (")]
        [InlineData("Data.Migrations.0005_CreateAppUser.sql", "CREATE TABLE dbo.APP_USER (")]
        [InlineData("Data.Migrations.0006_CreateSystem.sql", "CREATE TABLE dbo.EQUIPMENT (")]
        [InlineData("Data.Migrations.0006_CreateSystem.sql", "CREATE TABLE dbo.[SYSTEM] (")]
        [InlineData("Data.Migrations.0006_CreateSystem.sql", "CREATE TABLE dbo.SYSTEM_NODE (")]
        [InlineData("Data.Migrations.0007_SeedSystemDemo.sql", "INSERT INTO dbo.EQUIPMENT")]
        [InlineData("Data.Migrations.0008_SeedFaultCauseCategory.sql", "CREATE TABLE dbo.FAULT_CAUSE_CATEGORY")]
        public void Assembly_ContainsMigration(string resourceName, string expectedSql)
        {
            using (var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream(
                resourceName))
            {
                Assert.NotNull(stream);
                using (var reader = new StreamReader(stream))
                {
                    Assert.Contains(expectedSql, reader.ReadToEnd());
                }
            }
        }

        [Fact]
        public void Run_AppliesMigrationsInOrder_AndDoesNotReapplyThem()
        {
            var connectionString = Environment.GetEnvironmentVariable("MINIMES_TEST_SQL_CONNECTION_STRING");
            Assert.False(string.IsNullOrWhiteSpace(connectionString),
                "Set MINIMES_TEST_SQL_CONNECTION_STRING to a test SQL Server connection string with CREATE DATABASE permission.");

            // Always use a uniquely named disposable database, never the configured database.
            var databaseName = "MiniMES_MigrationTests_" + Guid.NewGuid().ToString("N");
            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = "master",
                Pooling = false
            };

            using (var admin = new SqlConnection(builder.ConnectionString))
            {
                admin.Open();
                Execute(admin, "CREATE DATABASE [" + databaseName + "]");
                try
                {
                    builder.InitialCatalog = databaseName;
                    MigrationRunner.Run(builder.ConnectionString);

                    using (var database = new SqlConnection(builder.ConnectionString))
                    {
                        database.Open();
                        AssertMigrationOrder(database);
                        VerifySystemConstraints(database);
                        VerifyFaultCauseCategory(database);
                        VerifyDemoTree(database);
                        Assert.Equal(1, Scalar(database,
                            "SELECT COUNT(*) FROM sys.foreign_keys " +
                            "WHERE name = 'FK_FAULT_HISTORY_APP_USER' " +
                            "AND parent_object_id = OBJECT_ID('dbo.FAULT_HISTORY') " +
                            "AND referenced_object_id = OBJECT_ID('dbo.APP_USER') " +
                            "AND is_disabled = 0 AND is_not_trusted = 0"));
                        Execute(database, "INSERT INTO dbo.APP_USER (Name) VALUES (N'Test User')");
                        Assert.Equal(1, Scalar(database,
                            "SELECT COUNT(*) FROM dbo.APP_USER WHERE Name = N'Test User' AND IsActive = 1"));
                        Assert.Equal(9, Scalar(database,
                            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.FAULT')"));
                        Assert.Equal(7, Scalar(database,
                            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.FAULT_HISTORY')"));
                        Assert.Equal(6, Scalar(database,
                            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ALARM_EVENT')"));
                        Assert.Equal(7, Scalar(database,
                            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ALARM')"));
                        Assert.Equal(8, Scalar(database,
                            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SAMPLE')"));
                        Assert.Equal(1, Scalar(database,
                            "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.SAMPLE') " +
                            "AND name = 'PK_SAMPLE' AND is_primary_key = 1 AND type = 2"));
                        Assert.Equal(1, Scalar(database,
                            "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.SAMPLE') " +
                            "AND name = 'IX_SAMPLE_Equipment_CollectedAt' AND type = 1"));
                        Execute(database,
                            "INSERT INTO dbo.SAMPLE (RunId, SequenceNumber, EquipmentId, CollectedAtUtc) " +
                            "VALUES (NEWID(), 1, 1, SYSUTCDATETIME())");
                    }

                    MigrationRunner.Run(builder.ConnectionString);

                    using (var database = new SqlConnection(builder.ConnectionString))
                    {
                        database.Open();
                        Assert.Equal(1, Scalar(database, "SELECT COUNT(*) FROM dbo.SAMPLE"));
                        Assert.Equal(6, Scalar(database, "SELECT COUNT(*) FROM dbo.FAULT_CAUSE_CATEGORY"));
                        VerifyDemoTree(database);
                        AssertMigrationOrder(database);
                    }
                }
                finally
                {
                    Execute(admin, "ALTER DATABASE [" + databaseName +
                        "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
                    Execute(admin, "DROP DATABASE [" + databaseName + "]");
                }
            }
        }

        private static void AssertMigrationOrder(SqlConnection connection)
        {
            var scripts = new List<string>();
            using (var command = new SqlCommand(
                "SELECT ScriptName FROM dbo.SchemaVersions ORDER BY Id", connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    scripts.Add(reader.GetString(0));
                }
            }

            Assert.Equal(new[]
            {
                "Data.Migrations.0001_CreateSample.sql",
                "Data.Migrations.0002_CreateAlarm.sql",
                "Data.Migrations.0003_CreateAlarmEvent.sql",
                "Data.Migrations.0004_CreateFault.sql",
                "Data.Migrations.0005_CreateAppUser.sql",
                "Data.Migrations.0006_CreateSystem.sql",
                "Data.Migrations.0007_SeedSystemDemo.sql",
                "Data.Migrations.0008_SeedFaultCauseCategory.sql"
            }, scripts);
        }

        private static void VerifyFaultCauseCategory(SqlConnection connection)
        {
            var categories = new List<string>();
            using (var command = new SqlCommand(
                "SELECT CauseCategoryId, Name FROM dbo.FAULT_CAUSE_CATEGORY ORDER BY CauseCategoryId", connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    categories.Add(reader.GetByte(0) + ":" + reader.GetString(1));
                }
            }
            Assert.Equal(new[] { "1:기계", "2:재료", "3:계측", "4:전기", "5:외부 영향", "6:기타" }, categories);

            var alarmId = Scalar(connection,
                "INSERT INTO dbo.ALARM (EquipmentId, Cause, RaisedAtUtc) OUTPUT INSERTED.AlarmId " +
                "VALUES (1, 1, SYSUTCDATETIME())");
            var insertFault = "INSERT INTO dbo.FAULT (AlarmId, Status, RegisteredAtUtc, CauseCategory) " +
                "VALUES (" + alarmId + ", 1, SYSUTCDATETIME(), ";
            var error = Assert.Throws<SqlException>(() => Execute(connection, insertFault + "7)"));
            Assert.Equal(547, error.Number);
            Assert.Contains("FK_FAULT_CauseCategory", error.Message);
            Execute(connection, insertFault + "1)");
        }

        private static void VerifyDemoTree(SqlConnection connection)
        {
            Assert.Equal(5, Scalar(connection,
                "SELECT COUNT(*) FROM dbo.EQUIPMENT WHERE EquipmentId BETWEEN 1 AND 5 " +
                "AND Name = N'설비 ' + CONVERT(nvarchar(10), EquipmentId)"));
            var nodes = new List<string>();
            using (var command = new SqlCommand(@"
WITH Tree AS (
    SELECT n.NodeId, n.SystemId, n.NodeType, n.EquipmentId,
           CAST(n.SortOrder AS varchar(100)) AS NodePath
    FROM dbo.SYSTEM_NODE n
    WHERE n.ParentNodeId IS NULL
    UNION ALL
    SELECT n.NodeId, n.SystemId, n.NodeType, n.EquipmentId,
           CAST(t.NodePath + '/' + CAST(n.SortOrder AS varchar(10)) AS varchar(100))
    FROM dbo.SYSTEM_NODE n
    JOIN Tree t ON n.ParentNodeId = t.NodeId AND n.SystemId = t.SystemId
)
SELECT s.Name, t.NodePath, t.NodeType, t.EquipmentId
FROM Tree t JOIN dbo.[SYSTEM] s ON s.SystemId = t.SystemId
WHERE s.Name IN (N'시스템 1', N'시스템 2')
ORDER BY s.Name, t.NodePath", connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    nodes.Add(reader.GetString(0) + ":" + reader.GetString(1) + ":" +
                        reader.GetByte(2) + ":" + (reader.IsDBNull(3) ? "-" : reader.GetInt32(3).ToString()));
                }
            }
            Assert.Equal(new[]
            {
                "시스템 1:0:2:-",
                "시스템 1:0/0:1:1",
                "시스템 1:0/1:3:-",
                "시스템 1:0/1/0:2:-",
                "시스템 1:0/1/0/0:1:2",
                "시스템 1:0/1/0/1:1:3",
                "시스템 1:0/1/1:1:4",
                "시스템 2:0:1:5"
            }, nodes);
        }

        private static void VerifySystemConstraints(SqlConnection connection)
        {
            Execute(connection, "INSERT INTO dbo.EQUIPMENT (EquipmentId, Name) VALUES (101, N'Equipment 101'), (102, N'Equipment 102')");
            var systemId = Scalar(connection,
                "INSERT INTO dbo.[SYSTEM] (Name) OUTPUT INSERTED.SystemId VALUES (N'System 1')");
            var rootId = Scalar(connection,
                "INSERT INTO dbo.SYSTEM_NODE (SystemId, NodeType, SortOrder) OUTPUT INSERTED.NodeId " +
                "VALUES (" + systemId + ", 2, 0)");
            var insertChild = "INSERT INTO dbo.SYSTEM_NODE " +
                "(SystemId, ParentNodeId, NodeType, EquipmentId, SortOrder) VALUES (" +
                systemId + ", " + rootId + ", ";
            Execute(connection, insertChild + "1, 101, 0)");

            Assert.Equal(2601, Assert.Throws<SqlException>(() =>
                Execute(connection, insertChild + "1, 101, 1)")).Number);
            Assert.Equal(2601, Assert.Throws<SqlException>(() => Execute(connection,
                "INSERT INTO dbo.SYSTEM_NODE (SystemId, NodeType, SortOrder) VALUES (" +
                systemId + ", 3, 1)")).Number);
            Assert.Equal(547, Assert.Throws<SqlException>(() =>
                Execute(connection, insertChild + "1, NULL, 1)")).Number);
            Assert.Equal(547, Assert.Throws<SqlException>(() =>
                Execute(connection, insertChild + "2, 102, 1)")).Number);
            Assert.Equal(547, Assert.Throws<SqlException>(() =>
                Execute(connection, insertChild + "4, NULL, 1)")).Number);
        }

        private static void Execute(SqlConnection connection, string sql)
        {
            using (var command = new SqlCommand(sql, connection))
            {
                command.ExecuteNonQuery();
            }
        }

        private static int Scalar(SqlConnection connection, string sql)
        {
            using (var command = new SqlCommand(sql, connection))
            {
                return (int)command.ExecuteScalar();
            }
        }
    }
}
