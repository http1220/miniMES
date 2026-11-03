using System;
using System.Collections.Generic;
using System.Data;
using Dapper;

namespace Data
{
    public sealed class AlarmRepository
    {
        private readonly IDbConnection connection;

        // The caller owns the connection and is responsible for disposing it.
        public AlarmRepository(IDbConnection connection)
        {
            this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        public AlarmAcknowledgementResult AcknowledgeAlarm(int alarmId, int userId, DateTime nowUtc)
        {
            var affectedRows = connection.Execute(@"
UPDATE dbo.ALARM
SET AcknowledgedAtUtc = @now, AcknowledgedBy = @userId
WHERE AlarmId = @alarmId AND AcknowledgedAtUtc IS NULL",
                new { now = nowUtc, userId, alarmId });

            if (affectedRows == 1)
            {
                return new AlarmAcknowledgementResult(AlarmAcknowledgementStatus.Success, userId);
            }

            if (affectedRows != 0)
            {
                throw new InvalidOperationException("Expected to acknowledge at most one alarm.");
            }

            // D-102: return the stored acknowledger, not the user who lost the update race.
            var existing = connection.QuerySingleOrDefault<AcknowledgementRow>(@"
SELECT AcknowledgedBy FROM dbo.ALARM WHERE AlarmId = @alarmId", new { alarmId });

            if (existing == null)
            {
                throw new KeyNotFoundException("The requested alarm does not exist.");
            }

            return new AlarmAcknowledgementResult(
                AlarmAcknowledgementStatus.AlreadyAcknowledged, existing.AcknowledgedBy);
        }

        private sealed class AcknowledgementRow
        {
            public int? AcknowledgedBy { get; set; }
        }
    }
}
