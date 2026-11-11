using System;
using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Data
{
    public sealed class FaultRepository
    {
        private readonly IDbConnection connection;

        // The caller owns and disposes the connection.
        public FaultRepository(IDbConnection connection)
        {
            this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        public FaultRegisterResult RegisterFault(int alarmId, byte causeCategory, string causeText,
            string actionText, DateTime nowUtc)
        {
            try
            {
                return BatchTransaction.Execute(connection, null, transaction =>
                {
                    if (!connection.QuerySingleOrDefault<int?>(
                        "SELECT AlarmId FROM dbo.ALARM WHERE AlarmId = @alarmId", new { alarmId }, transaction).HasValue)
                        return new FaultRegisterResult(FaultRegisterStatus.AlarmNotFound);
                    if (!CategoryExists(causeCategory, transaction))
                        return new FaultRegisterResult(FaultRegisterStatus.InvalidCauseCategory);
                    var existing = FindActiveFault(alarmId, null, transaction, true);
                    if (existing.HasValue)
                        return new FaultRegisterResult(FaultRegisterStatus.AlreadyRegistered, existing);
                    var parameters = new DynamicParameters(new { alarmId, causeCategory, causeText, actionText });
                    parameters.Add("nowUtc", nowUtc, DbType.DateTime2);
                    var row = connection.QuerySingle<FaultRow>(@"
INSERT INTO dbo.FAULT (AlarmId, Status, RegisteredAtUtc, CauseCategory, CauseText, ActionText)
OUTPUT inserted.FaultId, inserted.Version
VALUES (@alarmId, 1, @nowUtc, @causeCategory, @causeText, @actionText)", parameters, transaction);
                    return new FaultRegisterResult(FaultRegisterStatus.Success, row.FaultId, row.Version);
                });
            }
            catch (SqlException error) when (error.Number == 2601 || error.Number == 2627)
            {
                // The failed transaction has been disposed before reading the winning row.
                var existing = FindActiveFault(alarmId, null, null, false);
                if (!existing.HasValue)
                    throw;
                return new FaultRegisterResult(FaultRegisterStatus.AlreadyRegistered, existing);
            }
        }

        public FaultTextsUpdateResult UpdateTexts(int faultId, byte[] expectedVersion, byte causeCategory,
            string causeText, string actionText)
        {
            if (expectedVersion == null)
                throw new ArgumentNullException(nameof(expectedVersion));
            if (expectedVersion.Length != 8)
                throw new ArgumentException("A rowversion must contain exactly 8 bytes.", nameof(expectedVersion));
            return BatchTransaction.Execute(connection, null, transaction =>
            {
                var existing = connection.QuerySingleOrDefault<int?>(
                    "SELECT FaultId FROM dbo.FAULT WHERE FaultId = @faultId", new { faultId }, transaction);
                if (!existing.HasValue)
                    return new FaultTextsUpdateResult(FaultTextsUpdateStatus.NotFound);
                if (!CategoryExists(causeCategory, transaction))
                    return new FaultTextsUpdateResult(FaultTextsUpdateStatus.InvalidCauseCategory);
                var version = connection.QuerySingleOrDefault<byte[]>(@"
UPDATE dbo.FAULT SET CauseCategory = @causeCategory, CauseText = @causeText, ActionText = @actionText
OUTPUT inserted.Version
WHERE FaultId = @faultId AND Version = @expectedVersion AND Status <> 3",
                    new { faultId, expectedVersion, causeCategory, causeText, actionText }, transaction);
                if (version != null)
                    return new FaultTextsUpdateResult(FaultTextsUpdateStatus.Success,
                        causeCategory, causeText, actionText, version);
                var current = connection.QuerySingleOrDefault<FaultRow>(@"
SELECT Status, CauseCategory, CauseText, ActionText, Version FROM dbo.FAULT
WHERE FaultId = @faultId", new { faultId }, transaction);
                if (current == null)
                    return new FaultTextsUpdateResult(FaultTextsUpdateStatus.NotFound);
                return new FaultTextsUpdateResult(current.Status == 3
                    ? FaultTextsUpdateStatus.FaultClosed : FaultTextsUpdateStatus.Conflict,
                    current.CauseCategory, current.CauseText, current.ActionText, current.Version, current.Status);
            });
        }

        public FaultTransitionResult TransitionFault(int faultId, byte expectedCurrentStatus,
            byte newStatus, int changedByUserId, DateTime nowUtc, string reason, DateTime? recoveredAtUtc)
        {
            if (expectedCurrentStatus < 1 || expectedCurrentStatus > 4)
                throw new ArgumentOutOfRangeException(nameof(expectedCurrentStatus));
            if (newStatus < 1 || newStatus > 4)
                throw new ArgumentOutOfRangeException(nameof(newStatus));
            if ((newStatus == 3) != recoveredAtUtc.HasValue)
                throw new ArgumentException("Recovery time is required only when closing a fault.", nameof(recoveredAtUtc));
            try
            {
                return BatchTransaction.Execute(connection, null, transaction =>
                {
                    // Read the immutable alarm key before taking the shared active-fault range lock.
                    var alarmId = connection.QuerySingleOrDefault<int?>(
                        "SELECT AlarmId FROM dbo.FAULT WHERE FaultId = @faultId", new { faultId }, transaction);
                    if (!alarmId.HasValue)
                        return new FaultTransitionResult(FaultTransitionStatus.NotFound);
                    if (expectedCurrentStatus == 4 && newStatus == 1)
                    {
                        var active = FindActiveFault(alarmId.Value, faultId, transaction, true);
                        var status = connection.QuerySingle<byte>(
                            "SELECT Status FROM dbo.FAULT WHERE FaultId = @faultId", new { faultId }, transaction);
                        if (status != expectedCurrentStatus)
                            return new FaultTransitionResult(FaultTransitionStatus.StatusMismatch, status);
                        if (active.HasValue)
                            return new FaultTransitionResult(FaultTransitionStatus.ActiveFaultExists, status, active);
                    }
                    var parameters = new DynamicParameters(new { faultId, expectedCurrentStatus, newStatus, changedByUserId, reason });
                    parameters.Add("nowUtc", nowUtc, DbType.DateTime2);
                    parameters.Add("recoveredAtUtc", recoveredAtUtc, DbType.DateTime2);
                    var affected = connection.Execute(@"
UPDATE dbo.FAULT SET Status = @newStatus,
    RecoveredAtUtc = CASE WHEN @newStatus = 3 THEN @recoveredAtUtc
        WHEN Status = 3 THEN NULL ELSE RecoveredAtUtc END,
    ClosedAtUtc = CASE WHEN @newStatus = 3 THEN @nowUtc
        WHEN Status = 3 THEN NULL ELSE ClosedAtUtc END
WHERE FaultId = @faultId AND Status = @expectedCurrentStatus", parameters, transaction);
                    if (affected == 0)
                    {
                        var status = connection.QuerySingleOrDefault<byte?>(
                            "SELECT Status FROM dbo.FAULT WHERE FaultId = @faultId", new { faultId }, transaction);
                        return new FaultTransitionResult(status.HasValue
                            ? FaultTransitionStatus.StatusMismatch : FaultTransitionStatus.NotFound, status ?? 0);
                    }
                    connection.Execute(@"
INSERT INTO dbo.FAULT_HISTORY
    (FaultId, ChangedByUserId, ChangedAtUtc, PreviousStatus, NewStatus, Reason)
VALUES (@faultId, @changedByUserId, @nowUtc, @expectedCurrentStatus, @newStatus, @reason)", parameters, transaction);
                    return new FaultTransitionResult(FaultTransitionStatus.Success, newStatus);
                });
            }
            catch (SqlException error) when ((error.Number == 2601 || error.Number == 2627)
                && expectedCurrentStatus == 4 && newStatus == 1)
            {
                var alarmId = connection.QuerySingleOrDefault<int?>(
                    "SELECT AlarmId FROM dbo.FAULT WHERE FaultId = @faultId", new { faultId });
                var active = alarmId.HasValue ? FindActiveFault(alarmId.Value, faultId, null, false) : null;
                if (!active.HasValue)
                    throw;
                return new FaultTransitionResult(FaultTransitionStatus.ActiveFaultExists, 4, active);
            }
        }

        private bool CategoryExists(byte causeCategory, IDbTransaction transaction)
        {
            return connection.QuerySingleOrDefault<byte?>(@"
SELECT CauseCategoryId FROM dbo.FAULT_CAUSE_CATEGORY WHERE CauseCategoryId = @causeCategory",
                new { causeCategory }, transaction).HasValue;
        }

        private int? FindActiveFault(int alarmId, int? excludedFaultId, IDbTransaction transaction, bool locked)
        {
            return connection.QuerySingleOrDefault<int?>(
                "SELECT FaultId FROM dbo.FAULT" + (locked ? " WITH (UPDLOCK, HOLDLOCK)" : "") + @"
WHERE AlarmId = @alarmId AND Status <> 4 AND (@excludedFaultId IS NULL OR FaultId <> @excludedFaultId)",
                new { alarmId, excludedFaultId }, transaction);
        }

        private sealed class FaultRow
        {
            public int FaultId { get; set; }
            public byte Status { get; set; }
            public byte CauseCategory { get; set; }
            public string CauseText { get; set; }
            public string ActionText { get; set; }
            public byte[] Version { get; set; }
        }
    }
}
