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
                using (var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream("Data.Migrations.0009_AlterSampleMeasurementsToReal.sql"))
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

        [Fact]
        public void SaveSamples_ReplayedBatch_CountsEveryDuplicate()
        {
            var samples = new[] { NewSample(1), NewSample(2) };
            var repository = new SampleRepository(connection);
            var first = repository.SaveSamples(samples);
            var replay = repository.SaveSamples(samples);

            Assert.Equal(2, first.StoredCount);
            Assert.Equal(0, first.DuplicateCount);
            Assert.Equal(0, replay.StoredCount);
            Assert.Equal(2, replay.DuplicateCount);
            foreach (var sample in samples)
                Assert.Equal(1, connection.ExecuteScalar<int>(@"
SELECT COUNT(*) FROM dbo.SAMPLE
WHERE EquipmentId = @EquipmentId AND RunId = @RunId AND SequenceNumber = @SequenceNumber", sample));
        }

        [Fact]
        public void SaveSamples_RepeatedKeyInBatch_KeepsFirstValues()
        {
            var first = NewSample(1);
            first.Temperature = 12.34567f;
            var repeated = NewSample(1);
            repeated.RunId = first.RunId;
            repeated.Temperature = 99f;
            var result = new SampleRepository(connection).SaveSamples(new[] { first, repeated });

            Assert.Equal(1, result.StoredCount);
            Assert.Equal(1, result.DuplicateCount);
            Assert.Equal(first.Temperature, connection.QuerySingle<float?>("SELECT Temperature FROM dbo.SAMPLE"));
        }

        [Fact]
        public void SaveSamples_PreservesRealNullAndMillisecondValues()
        {
            var sample = NewSample(1234567890123L);
            sample.Temperature = 12.345678f;
            sample.Vibration = -0.12345678f;
            sample.StatusCode = 255;
            var empty = NewSample(2);
            new SampleRepository(connection).SaveSamples(new[] { sample, empty });
            var rows = connection.Query<SampleRecord>(@"
SELECT RunId, SequenceNumber, EquipmentId, CollectedAtUtc, Temperature, Vibration, StatusCode
FROM dbo.SAMPLE ORDER BY SequenceNumber DESC").ToArray();

            Assert.Equal(2, rows.Length);
            Assert.Equal(sample.RunId, rows[0].RunId);
            Assert.Equal(sample.SequenceNumber, rows[0].SequenceNumber);
            Assert.Equal(sample.EquipmentId, rows[0].EquipmentId);
            Assert.Equal(sample.CollectedAtUtc, rows[0].CollectedAtUtc);
            Assert.Equal(sample.Temperature, rows[0].Temperature);
            Assert.Equal(sample.Vibration, rows[0].Vibration);
            Assert.Equal(sample.StatusCode, rows[0].StatusCode);
            Assert.Null(rows[1].Temperature);
            Assert.Null(rows[1].Vibration);
            Assert.Null(rows[1].StatusCode);
            Assert.Equal(empty.CollectedAtUtc, rows[1].CollectedAtUtc);
        }

        [Fact]
        public void SaveSamples_CallerTransaction_CanRollBack()
        {
            using (var transaction = connection.BeginTransaction())
            {
                var result = new SampleRepository(connection).SaveSamples(new[] { NewSample(1) }, transaction);
                Assert.Equal(1, result.StoredCount);
                Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SAMPLE", transaction: transaction));
                transaction.Rollback();
            }
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SAMPLE"));
        }

        [Fact]
        public void SaveSamples_InvalidEntry_RollsBackWholeBatch()
        {
            Assert.Throws<ArgumentException>(() => new SampleRepository(connection)
                .SaveSamples(new[] { NewSample(1), null }));
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SAMPLE"));
        }

        [Fact]
        public void SaveSamples_ClosedConnection_RestoresClosedState()
        {
            connection.Close();
            var result = new SampleRepository(connection).SaveSamples(new[] { NewSample(1) });
            Assert.Equal(1, result.StoredCount);
            Assert.Equal(ConnectionState.Closed, connection.State);
            connection.Open();
            Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SAMPLE"));
        }

        private SampleRecord NewSample(long sequence)
        {
            return new SampleRecord
            {
                RunId = Guid.NewGuid(), SequenceNumber = sequence,
                EquipmentId = 1, CollectedAtUtc = fromUtc
            };
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
