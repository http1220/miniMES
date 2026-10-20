using System;
using System.IO;
using Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace IntegrationTests
{
    public class MigrationRunnerTests
    {
        [Fact]
        public void Assembly_ContainsSampleMigration()
        {
            using (var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream(
                "Data.Migrations.0001_CreateSample.sql"))
            {
                Assert.NotNull(stream);
                using (var reader = new StreamReader(stream))
                {
                    Assert.Contains("CREATE TABLE SAMPLE", reader.ReadToEnd());
                }
            }
        }

        [Fact]
        public void Run_CreatesSampleSchema_AndDoesNotReapplyMigration()
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
                        Assert.Equal(1, Scalar(database,
                            "SELECT COUNT(*) FROM dbo.SchemaVersions " +
                            "WHERE ScriptName = 'Data.Migrations.0001_CreateSample.sql'"));
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
