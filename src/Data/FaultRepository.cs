using System;
using System.Collections.Generic;
using System.Data;
using Dapper;

namespace Data
{
    public sealed class FaultRepository
    {
        private readonly IDbConnection connection;

        // The caller owns the connection and is responsible for disposing it.
        public FaultRepository(IDbConnection connection)
        {
            this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        public FaultActionTextUpdateResult UpdateActionText(int faultId, byte[] expectedVersion, string actionText)
        {
            if (expectedVersion == null)
                throw new ArgumentNullException(nameof(expectedVersion));
            if (expectedVersion.Length != 8)
                throw new ArgumentException("A rowversion must contain exactly 8 bytes.", nameof(expectedVersion));

            var newVersion = connection.QuerySingleOrDefault<byte[]>(@"
UPDATE dbo.FAULT SET ActionText = @actionText
OUTPUT inserted.Version
WHERE FaultId = @faultId AND Version = @expectedVersion",
                new { faultId, expectedVersion, actionText });

            if (newVersion != null)
                return new FaultActionTextUpdateResult(FaultActionTextUpdateStatus.Success, actionText, newVersion);

            // D-115: read both values together so the caller can resolve the conflict and retry.
            var current = connection.QuerySingleOrDefault<ActionTextRow>(
                "SELECT ActionText, Version FROM dbo.FAULT WHERE FaultId = @faultId", new { faultId });
            if (current == null)
                throw new KeyNotFoundException("The requested fault does not exist.");

            return new FaultActionTextUpdateResult(FaultActionTextUpdateStatus.Conflict, current.ActionText, current.Version);
        }

        private sealed class ActionTextRow
        {
            public string ActionText { get; set; }
            public byte[] Version { get; set; }
        }

        public FaultTransitionResult TransitionFault(int faultId, byte expectedCurrentStatus,
            byte newStatus, int changedByUserId, DateTime nowUtc, string reason)
        {
            if (expectedCurrentStatus < 1 || expectedCurrentStatus > 4)
                throw new ArgumentOutOfRangeException(nameof(expectedCurrentStatus));
            if (newStatus < 1 || newStatus > 4)
                throw new ArgumentOutOfRangeException(nameof(newStatus));

            var openedHere = connection.State == ConnectionState.Closed;
            try
            {
                if (openedHere)
                    connection.Open();

                // Disposing an uncommitted transaction rolls back, including INSERT failures.
                using (var transaction = connection.BeginTransaction())
                {
                    var affectedRows = connection.Execute(@"
UPDATE dbo.FAULT SET Status = @newStatus
WHERE FaultId = @faultId AND Status = @expectedCurrentStatus",
                        new { faultId, expectedCurrentStatus, newStatus }, transaction);

                    if (affectedRows == 1)
                    {
                        connection.Execute(@"
INSERT INTO dbo.FAULT_HISTORY
    (FaultId, ChangedByUserId, ChangedAtUtc, PreviousStatus, NewStatus, Reason)
VALUES (@faultId, @changedByUserId, @nowUtc, @expectedCurrentStatus, @newStatus, @reason)",
                            new { faultId, changedByUserId, nowUtc, expectedCurrentStatus, newStatus, reason },
                            transaction);
                        transaction.Commit();
                        return new FaultTransitionResult(FaultTransitionStatus.Success, newStatus);
                    }

                    if (affectedRows != 0)
                        throw new InvalidOperationException("Expected to transition at most one fault.");

                    var currentStatus = connection.QuerySingleOrDefault<byte?>(
                        "SELECT Status FROM dbo.FAULT WHERE FaultId = @faultId",
                        new { faultId }, transaction);
                    transaction.Rollback();

                    if (!currentStatus.HasValue)
                        throw new KeyNotFoundException("The requested fault does not exist.");

                    return new FaultTransitionResult(FaultTransitionStatus.StatusMismatch, currentStatus.Value);
                }
            }
            finally
            {
                if (openedHere)
                    connection.Close();
            }
        }
    }
}
