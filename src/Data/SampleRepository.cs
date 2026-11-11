using System;
using System.Collections.Generic;
using System.Data;
using Dapper;

namespace Data
{
    public sealed class SampleRepository
    {
        private readonly IDbConnection connection;

        // The caller owns and disposes the connection.
        public SampleRepository(IDbConnection connection)
        {
            this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        public SampleBatchResult SaveSamples(IEnumerable<SampleRecord> samples, DateTime recordedAtUtc)
        {
            return SaveSamples(samples, recordedAtUtc, null);
        }

        // A supplied transaction remains under caller control, including on failure.
        public SampleBatchResult SaveSamples(IEnumerable<SampleRecord> samples, DateTime recordedAtUtc, IDbTransaction transaction)
        {
            if (samples == null)
                throw new ArgumentNullException(nameof(samples));
            return BatchTransaction.Execute(connection, transaction, current =>
            {
                var stored = 0;
                var duplicates = 0;
                foreach (var sample in samples)
                {
                    if (sample == null)
                        throw new ArgumentException("Samples cannot contain null entries.", nameof(samples));
                    var parameters = new DynamicParameters(sample);
                    parameters.Add("RecordedAtUtc", recordedAtUtc, DbType.DateTime2);
                    parameters.Add("CollectedAtUtc", sample.CollectedAtUtc, DbType.DateTime2);
                    var count = connection.Execute(@"
INSERT INTO dbo.SAMPLE
    (RunId, SequenceNumber, EquipmentId, CollectedAtUtc, Temperature, Vibration, StatusCode, RecordedAtUtc)
SELECT @RunId, @SequenceNumber, @EquipmentId, @CollectedAtUtc, @Temperature, @Vibration, @StatusCode, @RecordedAtUtc
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SAMPLE WITH (UPDLOCK, HOLDLOCK)
    WHERE EquipmentId = @EquipmentId AND RunId = @RunId AND SequenceNumber = @SequenceNumber)",
                        parameters, current);
                    stored += count;
                    duplicates += count == 0 ? 1 : 0;
                }
                return new SampleBatchResult(stored, duplicates);
            });
        }

        // Stream raw samples in collection order within [fromUtc, toUtc).
        // Dispose the enumerator before reusing or closing the connection.
        public IEnumerable<SampleRow> StreamSamples(int equipmentId, DateTime fromUtc, DateTime toUtc)
        {
            var parameters = new DynamicParameters();
            parameters.Add("equipmentId", equipmentId);
            // Preserve millisecond precision at query boundaries.
            parameters.Add("fromUtc", fromUtc, DbType.DateTime2);
            parameters.Add("toUtc", toUtc, DbType.DateTime2);
            return connection.Query<SampleRow>(@"
SELECT CollectedAtUtc, StatusCode
FROM dbo.SAMPLE
WHERE EquipmentId = @equipmentId
  AND CollectedAtUtc >= @fromUtc AND CollectedAtUtc < @toUtc
ORDER BY CollectedAtUtc",
                parameters, buffered: false);
        }
    }
}
