using System;
using System.Data;
using System.IO;
using System.Linq;
using Dapper;
using Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace IntegrationTests
{
    public sealed class SampleRepositoryTests : IDisposable
    {
        private readonly string databaseName = "MiniMES_SampleTests_" + Guid.NewGuid().ToString("N");
        private readonly SqlConnection admin;
        private readonly SqlConnection connection;
        private readonly DateTime fromUtc = new DateTime(2026, 11, 5, 0, 0, 0, 1, DateTimeKind.Utc);

        public SampleRepositoryTests()
        {
            var configured = Environment.GetEnvironmentVariable("MINIMES_TEST_SQL_CONNECTION_STRING");
            Assert.False(string.IsNullOrWhiteSpace(configured),
                "Set MINIMES_TEST_SQL_CONNECTION_STRING to a test SQL Server connection string with CREATE DATABASE permission.");
            var builder = new SqlConnectionStringBuilder(configured)
            {
                InitialCatalog = "master",
                Pooling = false,
                MultipleActiveResultSets = false
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
                using (var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream("Data.Migrations.0001_CreateSample.sql"))
                using (var reader = new StreamReader(stream))
                    connection.Execute(reader.ReadToEnd());
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        [Fact]
        public void StreamSamples_FiltersEquipmentAndHalfOpenRange_OrdersAndPreservesRawValues()
        {
            InsertSample(1, fromUtc.AddSeconds(20), 255);
            InsertSample(1, fromUtc.AddSeconds(10), null);
            InsertSample(1, fromUtc, 0);
            InsertSample(1, fromUtc.AddMilliseconds(-1), 1);
            InsertSample(1, fromUtc.AddSeconds(30), 2);
            InsertSample(2, fromUtc.AddSeconds(5), 3);

            var rows = new SampleRepository(connection).StreamSamples(1, fromUtc, fromUtc.AddSeconds(30)).ToArray();

            Assert.Equal(new[] { fromUtc, fromUtc.AddSeconds(10), fromUtc.AddSeconds(20) },
                rows.Select(row => row.CollectedAtUtc).ToArray());
            Assert.Equal(new byte?[] { 0, null, 255 }, rows.Select(row => row.StatusCode).ToArray());
        }

        [Fact]
        public void StreamSamples_NoMatchingRows_ReturnsEmptySequence()
        {
            var repository = new SampleRepository(connection);
            Assert.Empty(repository.StreamSamples(1, fromUtc, fromUtc.AddDays(1)));
            InsertSample(1, fromUtc, 1);
            Assert.Empty(repository.StreamSamples(1, fromUtc, fromUtc));
        }

        [Fact]
        public void StreamSamples_DefersQuery_KeepsReaderOpenAndReleasesItOnEarlyDisposal()
        {
            var samples = new SampleRepository(connection).StreamSamples(1, fromUtc, fromUtc.AddDays(1));
            // Changes made before enumeration must be visible: the query has not run yet.
            InsertSample(1, fromUtc, 1);
            InsertSample(1, fromUtc.AddSeconds(1), 2);

            using (var enumerator = samples.GetEnumerator())
            {
                Assert.True(enumerator.MoveNext());
                Assert.Equal(fromUtc, enumerator.Current.CollectedAtUtc);
                // With MARS disabled, an active reader prevents another command on this connection.
                // A buffered query would already have closed its reader here.
                Assert.Throws<InvalidOperationException>(() => connection.ExecuteScalar<int>("SELECT 1"));
            }

            Assert.Equal(1, connection.ExecuteScalar<int>("SELECT 1"));
        }

        private void InsertSample(int equipmentId, DateTime collectedAtUtc, byte? statusCode)
        {
            var parameters = new DynamicParameters(new { runId = Guid.NewGuid(), equipmentId, statusCode });
            parameters.Add("collectedAtUtc", collectedAtUtc, DbType.DateTime2);
            connection.Execute(@"INSERT INTO dbo.SAMPLE (RunId, SequenceNumber, EquipmentId, CollectedAtUtc, StatusCode)
VALUES (@runId, 1, @equipmentId, @collectedAtUtc, @statusCode)",
                parameters);
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
