using System;
using System.Collections.Generic;
using System.Data;
using Core;
using Dapper;

namespace Data
{
    public sealed class DashboardReadRepository
    {
        private readonly IDbConnection connection;

        // The caller owns and disposes the connection.
        public DashboardReadRepository(IDbConnection connection)
        {
            this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        public IReadOnlyList<LatestEquipmentRow> GetLatestEquipment()
        {
            // Read only the latest collection time and its ties before applying the secondary sort.
            var rows = connection.Query<LatestEquipmentRow>(@"
SELECT e.EquipmentId, e.Name, s.StatusCode, s.Temperature, s.Vibration,
       s.CollectedAtUtc AS LastSampleAtUtc,
       CAST(CASE WHEN EXISTS (
           SELECT 1 FROM dbo.ALARM AS a
           WHERE a.EquipmentId = e.EquipmentId AND a.Cause = 1 AND a.ClearedAtUtc IS NULL
       ) THEN 1 ELSE 0 END AS bit) AS HasActiveCommunicationAlarm
FROM dbo.EQUIPMENT AS e
OUTER APPLY (
    SELECT TOP (1) recent.CollectedAtUtc, recent.StatusCode, recent.Temperature, recent.Vibration
    FROM (
        SELECT TOP (1) WITH TIES CollectedAtUtc, RecordedAtUtc, SequenceNumber,
               StatusCode, Temperature, Vibration
        FROM dbo.SAMPLE
        WHERE EquipmentId = e.EquipmentId
        ORDER BY CollectedAtUtc DESC
    ) AS recent
    ORDER BY recent.RecordedAtUtc DESC, recent.SequenceNumber DESC
) AS s
ORDER BY e.EquipmentId").AsList();
            foreach (var row in rows)
                row.LastSampleAtUtc = AsUtc(row.LastSampleAtUtc);
            return rows;
        }

        public IReadOnlyList<ActiveAlarmRow> GetActiveAlarms()
        {
            var rows = connection.Query<ActiveAlarmRow>(@"
SELECT a.AlarmId, a.EquipmentId, a.Cause, a.RaisedAtUtc, a.AcknowledgedAtUtc,
       a.AcknowledgedBy AS AcknowledgedByUserId, u.Name AS AcknowledgedByName, f.FaultId
FROM dbo.ALARM AS a
LEFT JOIN dbo.APP_USER AS u ON u.UserId = a.AcknowledgedBy
LEFT JOIN dbo.FAULT AS f ON f.AlarmId = a.AlarmId AND f.Status <> 4
WHERE a.ClearedAtUtc IS NULL
ORDER BY a.RaisedAtUtc DESC, a.AlarmId DESC").AsList();
            foreach (var row in rows)
            {
                row.RaisedAtUtc = AsUtc(row.RaisedAtUtc);
                row.AcknowledgedAtUtc = AsUtc(row.AcknowledgedAtUtc);
            }
            return rows;
        }

        public IReadOnlyList<ActiveUserRow> GetActiveUsers()
        {
            return connection.Query<ActiveUserRow>(@"
SELECT UserId, Name FROM dbo.APP_USER WHERE IsActive = 1
ORDER BY Name, UserId").AsList();
        }

        public IReadOnlyList<FaultListRow> ListFaults(FaultStatus? status, int limit)
        {
            var rows = connection.Query<FaultListRow>(@"
SELECT TOP (@limit) f.FaultId, f.AlarmId, a.EquipmentId, f.Status, a.RaisedAtUtc AS AlarmRaisedAtUtc,
       f.RegisteredAtUtc, f.CauseCategory, f.RecoveredAtUtc, f.Version
FROM dbo.FAULT AS f
INNER JOIN dbo.ALARM AS a ON a.AlarmId = f.AlarmId
WHERE @status IS NULL OR f.Status = @status
ORDER BY a.RaisedAtUtc DESC, f.FaultId DESC", new { status = (byte?)status, limit }).AsList();
            foreach (var row in rows)
                SetFaultTimesUtc(row);
            return rows;
        }

        public FaultDetailRow GetFaultDetail(int faultId)
        {
            var row = connection.QuerySingleOrDefault<FaultDetailRow>(@"
SELECT f.FaultId, f.AlarmId, a.EquipmentId, f.Status, a.RaisedAtUtc AS AlarmRaisedAtUtc,
       f.RegisteredAtUtc, f.CauseCategory, f.RecoveredAtUtc, f.Version, f.CauseText, f.ActionText,
       (SELECT TOP (1) h.Reason
        FROM dbo.FAULT_HISTORY AS h
        WHERE h.FaultId = f.FaultId AND h.NewStatus = 4
        ORDER BY h.ChangedAtUtc DESC, h.FaultHistoryId DESC) AS CancelReason
FROM dbo.FAULT AS f
INNER JOIN dbo.ALARM AS a ON a.AlarmId = f.AlarmId
WHERE f.FaultId = @faultId", new { faultId });
            if (row == null)
                return null;

            SetFaultTimesUtc(row);
            var history = connection.Query<FaultHistoryRow>(@"
SELECT h.ChangedAtUtc, u.Name AS ChangedByName, h.PreviousStatus, h.NewStatus, h.Reason
FROM dbo.FAULT_HISTORY AS h
INNER JOIN dbo.APP_USER AS u ON u.UserId = h.ChangedByUserId
WHERE h.FaultId = @faultId
ORDER BY h.ChangedAtUtc, h.FaultHistoryId", new { faultId }).AsList();
            foreach (var change in history)
                change.ChangedAtUtc = AsUtc(change.ChangedAtUtc);
            row.History = history;
            return row;
        }

        public IReadOnlyList<MetricFaultRow> GetFaultsForMetrics(int equipmentId, DateTime fromUtc, DateTime toUtc)
        {
            var parameters = new DynamicParameters(new { equipmentId });
            parameters.Add("fromUtc", fromUtc, DbType.DateTime2);
            parameters.Add("toUtc", toUtc, DbType.DateTime2);
            var rows = connection.Query<MetricFaultRow>(@"
SELECT a.RaisedAtUtc AS AlarmRaisedAtUtc, f.Status, f.RecoveredAtUtc
FROM dbo.FAULT AS f
INNER JOIN dbo.ALARM AS a ON a.AlarmId = f.AlarmId
WHERE a.EquipmentId = @equipmentId AND f.Status <> 4
  AND a.RaisedAtUtc < @toUtc
  AND (f.Status <> 3 OR f.RecoveredAtUtc >= @fromUtc)", parameters).AsList();
            foreach (var row in rows)
            {
                row.AlarmRaisedAtUtc = AsUtc(row.AlarmRaisedAtUtc);
                row.RecoveredAtUtc = AsUtc(row.RecoveredAtUtc);
            }
            return rows;
        }

        public IReadOnlyList<SystemNodeRow> GetSystemNodes()
        {
            return connection.Query<SystemNodeRow>(@"
SELECT NodeId, SystemId, ParentNodeId, NodeType, EquipmentId, SortOrder
FROM dbo.SYSTEM_NODE
ORDER BY SystemId, NodeId").AsList();
        }

        public SystemRow GetSystem(int systemId)
        {
            return connection.QuerySingleOrDefault<SystemRow>(@"
SELECT SystemId, Name FROM dbo.[SYSTEM] WHERE SystemId = @systemId", new { systemId });
        }

        public IReadOnlyList<int> GetEquipmentIds()
        {
            return connection.Query<int>("SELECT EquipmentId FROM dbo.EQUIPMENT ORDER BY EquipmentId").AsList();
        }

        private static void SetFaultTimesUtc(FaultListRow row)
        {
            row.AlarmRaisedAtUtc = AsUtc(row.AlarmRaisedAtUtc);
            row.RegisteredAtUtc = AsUtc(row.RegisteredAtUtc);
            row.RecoveredAtUtc = AsUtc(row.RecoveredAtUtc);
        }

        private static DateTime AsUtc(DateTime value)
        {
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static DateTime? AsUtc(DateTime? value)
        {
            return value.HasValue ? AsUtc(value.Value) : (DateTime?)null;
        }
    }

    public sealed class LatestEquipmentRow
    {
        public int EquipmentId { get; set; }
        public string Name { get; set; }
        public byte? StatusCode { get; set; }
        public float? Temperature { get; set; }
        public float? Vibration { get; set; }
        public DateTime? LastSampleAtUtc { get; set; }
        public bool HasActiveCommunicationAlarm { get; set; }
    }

    public sealed class ActiveAlarmRow
    {
        public int AlarmId { get; set; }
        public int EquipmentId { get; set; }
        public byte Cause { get; set; }
        public DateTime RaisedAtUtc { get; set; }
        public DateTime? AcknowledgedAtUtc { get; set; }
        public int? AcknowledgedByUserId { get; set; }
        public string AcknowledgedByName { get; set; }
        public int? FaultId { get; set; }
    }

    public sealed class ActiveUserRow
    {
        public int UserId { get; set; }
        public string Name { get; set; }
    }

    public class FaultListRow
    {
        public int FaultId { get; set; }
        public int AlarmId { get; set; }
        public int EquipmentId { get; set; }
        public FaultStatus Status { get; set; }
        public DateTime AlarmRaisedAtUtc { get; set; }
        public DateTime RegisteredAtUtc { get; set; }
        public byte CauseCategory { get; set; }
        public DateTime? RecoveredAtUtc { get; set; }
        public byte[] Version { get; set; }
    }

    public sealed class FaultDetailRow : FaultListRow
    {
        public string CauseText { get; set; }
        public string ActionText { get; set; }
        public string CancelReason { get; set; }
        public IReadOnlyList<FaultHistoryRow> History { get; set; }
    }

    public sealed class FaultHistoryRow
    {
        public DateTime ChangedAtUtc { get; set; }
        public string ChangedByName { get; set; }
        public FaultStatus PreviousStatus { get; set; }
        public FaultStatus NewStatus { get; set; }
        public string Reason { get; set; }
    }

    public sealed class MetricFaultRow
    {
        public DateTime AlarmRaisedAtUtc { get; set; }
        public FaultStatus Status { get; set; }
        public DateTime? RecoveredAtUtc { get; set; }
    }

    public sealed class SystemNodeRow
    {
        public int NodeId { get; set; }
        public int SystemId { get; set; }
        public int? ParentNodeId { get; set; }
        public byte NodeType { get; set; }
        public int? EquipmentId { get; set; }
        public int SortOrder { get; set; }
    }

    public sealed class SystemRow
    {
        public int SystemId { get; set; }
        public string Name { get; set; }
    }
}
