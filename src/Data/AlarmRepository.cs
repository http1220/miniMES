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
                AcknowledgementParameters(alarmId, userId, nowUtc));

            if (affectedRows == 1)
            {
                return new AlarmAcknowledgementResult(AlarmAcknowledgementStatus.Success, userId);
            }

            if (affectedRows != 0)
            {
                throw new InvalidOperationException("Expected to acknowledge at most one alarm.");
            }

            // Return the stored acknowledger after a lost update race.
            var existing = connection.QuerySingleOrDefault<AcknowledgementRow>(@"
SELECT AcknowledgedBy FROM dbo.ALARM WHERE AlarmId = @alarmId", new { alarmId });

            if (existing == null)
            {
                return new AlarmAcknowledgementResult(AlarmAcknowledgementStatus.NotFound, null);
            }

            return new AlarmAcknowledgementResult(
                AlarmAcknowledgementStatus.AlreadyAcknowledged, existing.AcknowledgedBy);
        }

        private static DynamicParameters AcknowledgementParameters(int alarmId, int userId, DateTime nowUtc)
        {
            var parameters = new DynamicParameters(new { alarmId, userId });
            parameters.Add("now", nowUtc, DbType.DateTime2);
            return parameters;
        }

        public AlarmEventBatchResult ApplyEvents(IEnumerable<AlarmEventRecord> events)
        {
            return ApplyEvents(events, null);
        }

        // A supplied transaction remains under caller control, including on failure.
        public AlarmEventBatchResult ApplyEvents(IEnumerable<AlarmEventRecord> events, IDbTransaction transaction)
        {
            if (events == null)
                throw new ArgumentNullException(nameof(events));
            var items = new List<AlarmEventRecord>();
            foreach (var item in events)
            {
                if (item == null || (item.EventType < 1 || item.EventType > 4))
                    throw new ArgumentException("Event type must be between 1 and 4.", nameof(events));
                items.Add(item);
            }

            return BatchTransaction.Execute(connection, transaction, current =>
            {
                var applied = 0;
                var duplicates = 0;
                var mismatches = new List<AlarmEventMismatch>();
                foreach (var item in items)
                {
                    var parameters = new DynamicParameters(item);
                    parameters.Add("OccurredAtUtc", item.OccurredAtUtc, DbType.DateTime2);
                    var inserted = connection.Execute(@"
INSERT INTO dbo.ALARM_EVENT (RunId, EventSequence, EquipmentId, Cause, EventType, OccurredAtUtc)
SELECT @RunId, @EventSequence, @EquipmentId, @Cause, @EventType, @OccurredAtUtc
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.ALARM_EVENT WITH (UPDLOCK, HOLDLOCK)
    WHERE RunId = @RunId AND EventSequence = @EventSequence)", parameters, current);
                    if (inserted == 0)
                    {
                        duplicates++;
                        continue;
                    }

                    applied++;
                    var activeId = connection.QuerySingleOrDefault<int?>(@"
SELECT AlarmId FROM dbo.ALARM WITH (UPDLOCK, HOLDLOCK)
WHERE EquipmentId = @EquipmentId AND Cause = @Cause AND ClearedAtUtc IS NULL",
                        parameters, current);
                    int? changedAlarmId = null;
                    if ((item.EventType == 1 || item.EventType == 3) && !activeId.HasValue)
                    {
                        changedAlarmId = connection.QuerySingle<int>(@"
INSERT INTO dbo.ALARM (EquipmentId, Cause, RaisedAtUtc)
OUTPUT INSERTED.AlarmId
VALUES (@EquipmentId, @Cause, @OccurredAtUtc)", parameters, current);
                    }
                    else if ((item.EventType == 2 || item.EventType == 4) && activeId.HasValue)
                    {
                        changedAlarmId = activeId.Value;
                        parameters.Add("AlarmId", activeId.Value);
                        connection.Execute(@"
UPDATE dbo.ALARM SET ClearedAtUtc = @OccurredAtUtc WHERE AlarmId = @AlarmId",
                            parameters, current);
                    }
                    else if (item.EventType == 1 || item.EventType == 2)
                    {
                        mismatches.Add(new AlarmEventMismatch(item));
                    }
                    if (changedAlarmId.HasValue)
                    {
                        parameters.Add("AlarmId", changedAlarmId.Value);
                        connection.Execute(@"
UPDATE dbo.ALARM_EVENT SET AlarmId = @AlarmId
WHERE RunId = @RunId AND EventSequence = @EventSequence", parameters, current);
                    }
                }
                return new AlarmEventBatchResult(applied, duplicates, mismatches);
            });
        }

        private sealed class AcknowledgementRow
        {
            public int? AcknowledgedBy { get; set; }
        }
    }
}
