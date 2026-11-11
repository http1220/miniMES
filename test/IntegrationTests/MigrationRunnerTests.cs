using System;
using System.Collections.Generic;
using System.IO;
using Dapper;
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
        [InlineData("Data.Migrations.0009_AlterSampleMeasurementsToReal.sql", "ALTER TABLE dbo.SAMPLE ALTER COLUMN Temperature REAL NULL;")]
        [InlineData("Data.Migrations.0009_AlterSampleMeasurementsToReal.sql", "ALTER TABLE dbo.SAMPLE ALTER COLUMN Vibration REAL NULL;")]
        [InlineData("Data.Migrations.0010_AddCollectorRunAndEventLinks.sql", "CREATE TABLE dbo.COLLECTOR_RUN")]
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
                        VerifyCollectorConstraints(database);
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
                        Assert.Equal(7, Scalar(database,
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
                            "DECLARE @runId uniqueidentifier = NEWID(); " +
                            "INSERT INTO dbo.COLLECTOR_RUN VALUES (@runId, SYSUTCDATETIME()); " +
                            "INSERT INTO dbo.SAMPLE (RunId, SequenceNumber, EquipmentId, CollectedAtUtc, RecordedAtUtc) " +
                            "VALUES (@runId, 1, 1, SYSUTCDATETIME(), SYSUTCDATETIME())");
                    }

                    MigrationRunner.Run(builder.ConnectionString);

                    using (var database = new SqlConnection(builder.ConnectionString))
                    {
                        database.Open();
                        Assert.Equal(1, Scalar(database, "SELECT COUNT(*) FROM dbo.SAMPLE"));
                        Assert.Equal(6, Scalar(database, "SELECT COUNT(*) FROM dbo.FAULT_CAUSE_CATEGORY"));
                        VerifyDemoTree(database);
                        AssertMigrationOrder(database);
                        VerifyCollectorConstraints(database);
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

        [Fact]
        public void Run_SampleMeasurements_UseNullableRealAndRoundTripSingleValues()
        {
            var connectionString = Environment.GetEnvironmentVariable("MINIMES_TEST_SQL_CONNECTION_STRING");
            Assert.False(string.IsNullOrWhiteSpace(connectionString),
                "Set MINIMES_TEST_SQL_CONNECTION_STRING to a test SQL Server connection string with CREATE DATABASE permission.");

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
                        foreach (var columnName in new[] { "Temperature", "Vibration" })
                        {
                            var column = database.QuerySingle(@"
SELECT t.name AS TypeName, c.is_nullable AS IsNullable
FROM sys.columns c
JOIN sys.types t ON c.user_type_id = t.user_type_id
WHERE c.object_id = OBJECT_ID(@tableName) AND c.name = @columnName",
                                new { tableName = "dbo.SAMPLE", columnName });
                            Assert.Equal("real", (string)column.TypeName);
                            Assert.True((bool)column.IsNullable);
                        }

                        var runId = Guid.NewGuid();
                        var collectedAtUtc = new DateTime(2026, 11, 6, 1, 2, 3, DateTimeKind.Utc);
                        const int equipmentId = 1;
                        new CollectorRunRepository(database).EnsureRun(runId, collectedAtUtc);
                        const string insertSample = @"
INSERT INTO dbo.SAMPLE
    (RunId, SequenceNumber, EquipmentId, CollectedAtUtc, Temperature, Vibration, RecordedAtUtc)
VALUES (@runId, @sequenceNumber, @equipmentId, @collectedAtUtc, @temperature, @vibration, @collectedAtUtc)";
                        var values = new[] { 23.456f, 1234.5f };
                        for (var index = 0; index < values.Length; index++)
                        {
                            var expected = values[index];
                            var parameters = new
                            {
                                runId, sequenceNumber = (long)index + 1, equipmentId, collectedAtUtc,
                                temperature = expected, vibration = expected
                            };
                            database.Execute(insertSample, parameters);
                            Assert.Equal(expected, database.QuerySingle<float>(@"
SELECT Temperature FROM dbo.SAMPLE
WHERE RunId = @runId AND SequenceNumber = @sequenceNumber AND EquipmentId = @equipmentId",
                                parameters));
                            Assert.Equal(expected, database.QuerySingle<float>(@"
SELECT Vibration FROM dbo.SAMPLE
WHERE RunId = @runId AND SequenceNumber = @sequenceNumber AND EquipmentId = @equipmentId",
                                parameters));
                        }

                        var nullParameters = new
                        {
                            runId, sequenceNumber = 3L, equipmentId, collectedAtUtc,
                            temperature = (float?)null, vibration = (float?)null
                        };
                        database.Execute(insertSample, nullParameters);
                        Assert.Null(database.QuerySingle<float?>(@"
SELECT Temperature FROM dbo.SAMPLE
WHERE RunId = @runId AND SequenceNumber = @sequenceNumber AND EquipmentId = @equipmentId",
                            nullParameters));
                        Assert.Null(database.QuerySingle<float?>(@"
SELECT Vibration FROM dbo.SAMPLE
WHERE RunId = @runId AND SequenceNumber = @sequenceNumber AND EquipmentId = @equipmentId",
                            nullParameters));
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

        private static void VerifyCollectorConstraints(SqlConnection database)
        {
            Assert.Equal(2, Scalar(database, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.COLLECTOR_RUN')"));
            Assert.Equal(1, Scalar(database, @"SELECT COUNT(*) FROM sys.indexes
WHERE object_id = OBJECT_ID('dbo.COLLECTOR_RUN') AND name = 'PK_COLLECTOR_RUN' AND is_primary_key = 1"));
            Assert.Equal(1, Scalar(database, @"SELECT COUNT(*) FROM sys.index_columns ic
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
WHERE i.object_id = OBJECT_ID('dbo.COLLECTOR_RUN') AND i.is_primary_key = 1 AND c.name = 'RunId' AND ic.key_ordinal = 1"));
            foreach (var pair in new[]
            {
                new[] { "SAMPLE", "COLLECTOR_RUN", "RunId", "RunId" },
                new[] { "ALARM_EVENT", "COLLECTOR_RUN", "RunId", "RunId" },
                new[] { "SAMPLE", "EQUIPMENT", "EquipmentId", "EquipmentId" },
                new[] { "ALARM", "EQUIPMENT", "EquipmentId", "EquipmentId" },
                new[] { "ALARM_EVENT", "ALARM", "AlarmId", "AlarmId" }
            })
            {
                Assert.Equal(1, database.ExecuteScalar<int>(@"SELECT COUNT(*) FROM sys.foreign_keys f
JOIN sys.foreign_key_columns fc ON fc.constraint_object_id = f.object_id
JOIN sys.columns child ON child.object_id = fc.parent_object_id AND child.column_id = fc.parent_column_id
JOIN sys.columns parent ON parent.object_id = fc.referenced_object_id AND parent.column_id = fc.referenced_column_id
WHERE f.name = @name AND f.parent_object_id = OBJECT_ID(@childTable)
AND f.referenced_object_id = OBJECT_ID(@parentTable) AND child.name = @childColumn AND parent.name = @parentColumn
AND f.is_disabled = 0 AND f.is_not_trusted = 0",
                    new { name = "FK_" + pair[0] + "_" + pair[1], childTable = "dbo." + pair[0], parentTable = "dbo." + pair[1], childColumn = pair[2], parentColumn = pair[3] }));
            }
            Assert.Equal(0, Scalar(database, @"SELECT COUNT(*) FROM sys.default_constraints d
JOIN sys.columns c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
WHERE d.parent_object_id = OBJECT_ID('dbo.SAMPLE') AND c.name = 'RecordedAtUtc'"));
            using (var transaction = database.BeginTransaction())
            {
                var runId = Guid.NewGuid();
                new CollectorRunRepository(database).EnsureRun(runId, DateTime.UtcNow, transaction);
                const string insertEvent = @"INSERT INTO dbo.ALARM_EVENT
(RunId, EventSequence, EquipmentId, Cause, EventType, OccurredAtUtc)
VALUES (@runId, @sequence, 1, 1, @type, SYSUTCDATETIME())";
                Assert.Equal(1, database.Execute(insertEvent, new { runId, sequence = 1, type = 4 }, transaction));
                Assert.Equal(547, Assert.Throws<SqlException>(() => database.Execute(insertEvent,
                    new { runId, sequence = 2, type = 5 }, transaction)).Number);
                Assert.Equal(515, Assert.Throws<SqlException>(() => database.Execute(@"INSERT INTO dbo.SAMPLE
(RunId, SequenceNumber, EquipmentId, CollectedAtUtc) VALUES (@runId, 987654321, 1, SYSUTCDATETIME())",
                    new { runId }, transaction)).Number);
                transaction.Rollback();
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
                "Data.Migrations.0008_SeedFaultCauseCategory.sql",
                "Data.Migrations.0009_AlterSampleMeasurementsToReal.sql",
                "Data.Migrations.0010_AddCollectorRunAndEventLinks.sql"
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
            const string insertFault = @"
INSERT INTO dbo.FAULT (AlarmId, Status, RegisteredAtUtc, CauseCategory)
VALUES (@alarmId, @status, @registeredAtUtc, @causeCategory)";
            var registeredAtUtc = DateTime.UtcNow;
            var error = Assert.Throws<SqlException>(() => connection.Execute(insertFault,
                new { alarmId, status = 1, registeredAtUtc, causeCategory = 7 }));
            Assert.Equal(547, error.Number);
            Assert.Contains("FK_FAULT_CauseCategory", error.Message);
            connection.Execute(insertFault,
                new { alarmId, status = 1, registeredAtUtc, causeCategory = 1 });
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
            var rootId = connection.QuerySingle<int>(@"
INSERT INTO dbo.SYSTEM_NODE (SystemId, NodeType, SortOrder)
OUTPUT INSERTED.NodeId VALUES (@systemId, @nodeType, @sortOrder)",
                new { systemId, nodeType = 2, sortOrder = 0 });
            const string insertChild = @"
INSERT INTO dbo.SYSTEM_NODE (SystemId, ParentNodeId, NodeType, EquipmentId, SortOrder)
VALUES (@systemId, @rootId, @nodeType, @equipmentId, @sortOrder)";
            connection.Execute(insertChild,
                new { systemId, rootId, nodeType = 1, equipmentId = 101, sortOrder = 0 });

            Assert.Equal(2601, Assert.Throws<SqlException>(() => connection.Execute(insertChild,
                new { systemId, rootId, nodeType = 1, equipmentId = 101, sortOrder = 1 })).Number);
            Assert.Equal(2601, Assert.Throws<SqlException>(() => connection.Execute(@"
INSERT INTO dbo.SYSTEM_NODE (SystemId, NodeType, SortOrder)
VALUES (@systemId, @nodeType, @sortOrder)",
                new { systemId, nodeType = 3, sortOrder = 1 })).Number);
            Assert.Equal(547, Assert.Throws<SqlException>(() => connection.Execute(insertChild,
                new { systemId, rootId, nodeType = 1, equipmentId = (int?)null, sortOrder = 1 })).Number);
            Assert.Equal(547, Assert.Throws<SqlException>(() => connection.Execute(insertChild,
                new { systemId, rootId, nodeType = 2, equipmentId = 102, sortOrder = 1 })).Number);
            Assert.Equal(547, Assert.Throws<SqlException>(() => connection.Execute(insertChild,
                new { systemId, rootId, nodeType = 4, equipmentId = (int?)null, sortOrder = 1 })).Number);
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
