using System;
using System.Data;
using System.Linq;
using Core;
using Dapper;
using Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace IntegrationTests
{
    public sealed class DashboardReadRepositoryTests : IDisposable
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private readonly string databaseName = "MiniMES_DashboardReadTests_" + Guid.NewGuid().ToString("N");
        private readonly SqlConnection admin;
        private readonly SqlConnection connection;
        private readonly DashboardReadRepository repository;

        public DashboardReadRepositoryTests()
        {
            var configured = Environment.GetEnvironmentVariable("MINIMES_TEST_SQL_CONNECTION_STRING");
            Assert.False(string.IsNullOrWhiteSpace(configured),
                "Set MINIMES_TEST_SQL_CONNECTION_STRING to a test SQL Server connection string with CREATE DATABASE permission.");
            var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master", Pooling = false };
            admin = new SqlConnection(builder.ConnectionString);
            admin.Open();
            admin.Execute("CREATE DATABASE [" + databaseName + "]");
            builder.InitialCatalog = databaseName;
            connection = new SqlConnection(builder.ConnectionString);
            try
            {
                connection.Open();
                MigrationRunner.Run(builder.ConnectionString);
                repository = new DashboardReadRepository(connection);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        [Fact]
        public void GetLatestEquipment_ReturnsAllEquipmentWithLatestSampleAndCommunicationAlarm()
        {
            var runId = Guid.NewGuid();
            connection.Execute(@"
INSERT INTO dbo.COLLECTOR_RUN (RunId, StartedAtUtc) VALUES (@runId, @now);
INSERT INTO dbo.SAMPLE
    (RunId, SequenceNumber, EquipmentId, CollectedAtUtc, RecordedAtUtc, Temperature, Vibration, StatusCode)
VALUES
    (@runId, 1, 1, @now, DATEADD(second, 9, @now), 99, 9, 0),
    (@runId, 9, 1, DATEADD(second, 1, @now), DATEADD(second, 2, @now), 88, 8, 0),
    (@runId, 4, 1, DATEADD(second, 1, @now), DATEADD(second, 3, @now), 77, 7, 1),
    (@runId, 5, 1, DATEADD(second, 1, @now), DATEADD(second, 3, @now), 42.5, 1.25, 2),
    (@runId, 6, 2, @now, @now, NULL, NULL, NULL)", new { runId, now = Now });
            InsertAlarm(1, 1, Now);
            InsertAlarm(2, 1, Now, cleared: true);
            InsertAlarm(3, 2, Now);

            var rows = repository.GetLatestEquipment();

            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, rows.Select(row => row.EquipmentId));
            Assert.Equal("설비 1", rows[0].Name);
            Assert.Equal<byte?>(2, rows[0].StatusCode);
            Assert.Equal<float?>(42.5f, rows[0].Temperature);
            Assert.Equal<float?>(1.25f, rows[0].Vibration);
            AssertUtc(Now.AddSeconds(1), rows[0].LastSampleAtUtc.Value);
            AssertUtc(Now, rows[1].LastSampleAtUtc.Value);
            Assert.Null(rows[1].StatusCode);
            Assert.Null(rows[1].Temperature);
            Assert.Null(rows[1].Vibration);
            Assert.Null(rows[2].StatusCode);
            Assert.Null(rows[2].Temperature);
            Assert.Null(rows[2].Vibration);
            Assert.Null(rows[2].LastSampleAtUtc);
            Assert.True(rows[0].HasActiveCommunicationAlarm);
            Assert.All(rows.Skip(1), row => Assert.False(row.HasActiveCommunicationAlarm));
        }

        [Fact]
        public void GetActiveAlarms_ExcludesClearedAndJoinsAcknowledgementAndNonCancelledFault()
        {
            var userId = InsertUser("Operator", true);
            var earlier = InsertAlarm(1, 1, Now.AddSeconds(-1));
            var cancelledOnly = InsertAlarm(1, 2, Now);
            var acknowledged = InsertAlarm(1, 3, Now, acknowledgedBy: userId);
            InsertAlarm(2, 1, Now.AddSeconds(1), cleared: true);
            InsertFault(cancelledOnly, FaultStatus.Cancelled);
            InsertFault(acknowledged, FaultStatus.Cancelled);
            var faultId = InsertFault(acknowledged, FaultStatus.InProgress);

            var rows = repository.GetActiveAlarms();

            Assert.Equal(new[] { acknowledged, cancelledOnly, earlier }, rows.Select(row => row.AlarmId));
            Assert.Equal(1, rows[0].EquipmentId);
            Assert.Equal((byte)3, rows[0].Cause);
            AssertUtc(Now, rows[0].RaisedAtUtc);
            AssertUtc(Now, rows[0].AcknowledgedAtUtc.Value);
            Assert.Equal<int?>(userId, rows[0].AcknowledgedByUserId);
            Assert.Equal("Operator", rows[0].AcknowledgedByName);
            Assert.Equal<int?>(faultId, rows[0].FaultId);
            Assert.Null(rows[1].FaultId);
            Assert.Null(rows[1].AcknowledgedAtUtc);
            Assert.Null(rows[1].AcknowledgedByUserId);
            Assert.Null(rows[1].AcknowledgedByName);
            Assert.Null(rows[2].FaultId);
        }

        [Fact]
        public void GetActiveUsers_ExcludesInactiveAndSortsByNameThenId()
        {
            var zoe = InsertUser("Zoe", true);
            InsertUser("Aaron", false);
            var ada = InsertUser("Ada", true);
            var secondAda = InsertUser("Ada", true);

            var rows = repository.GetActiveUsers();

            Assert.Equal(new[] { ada, secondAda, zoe }, rows.Select(row => row.UserId));
            Assert.Equal(new[] { "Ada", "Ada", "Zoe" }, rows.Select(row => row.Name));
        }

        [Fact]
        public void ListFaults_FiltersBeforeLimitAndSortsByAlarmTimeThenFaultId()
        {
            var alarmId = InsertAlarm(1, 1, Now);
            var first = InsertFault(alarmId, FaultStatus.Closed, Now.AddSeconds(1));
            var tied = InsertFault(InsertAlarm(2, 1, Now), FaultStatus.Registered);
            var older = InsertFault(InsertAlarm(1, 2, Now.AddSeconds(-1)), FaultStatus.Closed, Now);

            Assert.Equal(new[] { tied, first }, repository.ListFaults(null, 2).Select(item => item.FaultId));
            Assert.Null(repository.ListFaults(null, 1)[0].RecoveredAtUtc);
            var row = Assert.Single(repository.ListFaults(FaultStatus.Closed, 1));
            Assert.Equal(first, row.FaultId);
            Assert.Equal(alarmId, row.AlarmId);
            Assert.Equal(1, row.EquipmentId);
            Assert.Equal(FaultStatus.Closed, row.Status);
            Assert.Equal((byte)1, row.CauseCategory);
            Assert.Equal(8, row.Version.Length);
            AssertUtc(Now, row.AlarmRaisedAtUtc);
            AssertUtc(Now.AddMinutes(1), row.RegisteredAtUtc);
            AssertUtc(Now.AddSeconds(1), row.RecoveredAtUtc.Value);
            Assert.Equal(new[] { first, older }, repository.ListFaults(FaultStatus.Closed, 200).Select(item => item.FaultId));
            Assert.Empty(repository.ListFaults(FaultStatus.Cancelled, 200));
        }

        [Fact]
        public void GetFaultDetail_ReturnsOrderedHistoryAndLastCancellationReason()
        {
            var firstUser = InsertUser("First", true);
            var secondUser = InsertUser("Second", false);
            var fault = InsertFault(InsertAlarm(1, 1, Now), FaultStatus.Cancelled);
            var noHistory = InsertFault(InsertAlarm(1, 2, Now), FaultStatus.Registered);

            InsertHistory(fault, secondUser, Now.AddSeconds(1), FaultStatus.InProgress, FaultStatus.Cancelled, "first cancel");
            InsertHistory(fault, firstUser, Now, FaultStatus.Registered, FaultStatus.InProgress, null);
            InsertHistory(fault, firstUser, Now.AddSeconds(1), FaultStatus.Cancelled, FaultStatus.Registered, "restored");
            InsertHistory(fault, secondUser, Now.AddSeconds(1), FaultStatus.Registered, FaultStatus.Cancelled, "last cancel");

            Assert.Null(repository.GetFaultDetail(-1));
            var row = repository.GetFaultDetail(fault);
            Assert.Equal(fault, row.FaultId);
            Assert.Equal("cause text", row.CauseText);
            Assert.Equal("action text", row.ActionText);
            Assert.Equal("last cancel", row.CancelReason);
            Assert.Equal(new[] { "First", "Second", "First", "Second" }, row.History.Select(change => change.ChangedByName));
            Assert.Equal(new[] { null, "first cancel", "restored", "last cancel" }, row.History.Select(change => change.Reason));
            Assert.Equal(FaultStatus.Cancelled, row.History[2].PreviousStatus);
            Assert.Equal(FaultStatus.Registered, row.History[2].NewStatus);
            AssertUtc(Now, row.History[0].ChangedAtUtc);
            AssertUtc(Now.AddSeconds(1), row.History[3].ChangedAtUtc);
            var empty = repository.GetFaultDetail(noHistory);
            Assert.Null(empty.CancelReason);
            Assert.Empty(empty.History);
        }

        [Fact]
        public void GetFaultsForMetrics_IncludesOnlyOverlappingNonCancelledFaultsForEquipment()
        {
            var from = Now.AddMilliseconds(1);
            var to = Now.AddSeconds(1).AddMilliseconds(1);
            var ongoingAt = from.AddDays(-3);
            var boundaryRecoveryAt = from.AddDays(-2);
            var beforeEndAt = to.AddMilliseconds(-1);
            InsertFault(InsertAlarm(1, 1, ongoingAt, cleared: true), FaultStatus.InProgress);
            InsertFault(InsertAlarm(1, 1, boundaryRecoveryAt, cleared: true), FaultStatus.Closed, from);
            InsertFault(InsertAlarm(1, 1, beforeEndAt, cleared: true), FaultStatus.Registered);
            InsertFault(InsertAlarm(1, 1, from.AddDays(-1), cleared: true), FaultStatus.Closed, from.AddMilliseconds(-1));
            InsertFault(InsertAlarm(1, 1, from, cleared: true), FaultStatus.Cancelled);
            InsertFault(InsertAlarm(1, 1, to, cleared: true), FaultStatus.Registered);
            InsertFault(InsertAlarm(1, 1, to.AddMilliseconds(1), cleared: true), FaultStatus.InProgress);
            InsertFault(InsertAlarm(2, 1, from, cleared: true), FaultStatus.InProgress);

            var rows = repository.GetFaultsForMetrics(1, from, to).OrderBy(row => row.AlarmRaisedAtUtc).ToArray();

            Assert.Equal(new[] { ongoingAt, boundaryRecoveryAt, beforeEndAt }, rows.Select(row => row.AlarmRaisedAtUtc));
            Assert.Equal(new[] { FaultStatus.InProgress, FaultStatus.Closed, FaultStatus.Registered }, rows.Select(row => row.Status));
            Assert.All(rows, row => Assert.Equal(DateTimeKind.Utc, row.AlarmRaisedAtUtc.Kind));
            Assert.Null(rows[0].RecoveredAtUtc);
            AssertUtc(from, rows[1].RecoveredAtUtc.Value);
            Assert.Empty(repository.GetFaultsForMetrics(3, from, to));
        }

        [Fact]
        public void SystemAndEquipmentReads_ReturnSeededSystemsAndOrderedNodesAndIds()
        {
            var nodes = repository.GetSystemNodes();

            Assert.Equal(Enumerable.Range(1, 8), nodes.Select(node => node.NodeId));
            Assert.Equal(new[] { 1, 1, 1, 1, 1, 1, 1, 2 }, nodes.Select(node => node.SystemId));
            Assert.Equal(new int?[] { null, 1, 1, 3, 4, 4, 3, null }, nodes.Select(node => node.ParentNodeId));
            Assert.Equal(new byte[] { 2, 1, 3, 2, 1, 1, 1, 1 }, nodes.Select(node => node.NodeType));
            Assert.Equal(new int?[] { null, 1, null, null, 2, 3, 4, 5 }, nodes.Select(node => node.EquipmentId));
            Assert.Equal(new[] { 0, 0, 1, 0, 0, 1, 1, 0 }, nodes.Select(node => node.SortOrder));
            var first = repository.GetSystem(1);
            var second = repository.GetSystem(2);
            Assert.Equal(1, first.SystemId);
            Assert.Equal("시스템 1", first.Name);
            Assert.Equal(2, second.SystemId);
            Assert.Equal("시스템 2", second.Name);
            Assert.Null(repository.GetSystem(-1));
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, repository.GetEquipmentIds());
        }

        private int InsertUser(string name, bool isActive)
        {
            return connection.QuerySingle<int>(@"
INSERT INTO dbo.APP_USER (Name, IsActive)
OUTPUT inserted.UserId VALUES (@name, @isActive)", new { name, isActive });
        }

        private int InsertAlarm(int equipmentId, byte cause, DateTime raisedAtUtc,
            bool cleared = false, int? acknowledgedBy = null)
        {
            var parameters = new DynamicParameters(new { equipmentId, cause, acknowledgedBy });
            parameters.Add("raisedAtUtc", raisedAtUtc, DbType.DateTime2);
            parameters.Add("clearedAtUtc", cleared ? Now.AddMinutes(1) : (DateTime?)null, DbType.DateTime2);
            parameters.Add("acknowledgedAtUtc", acknowledgedBy.HasValue ? Now : (DateTime?)null, DbType.DateTime2);
            return connection.QuerySingle<int>(@"
INSERT INTO dbo.ALARM (EquipmentId, Cause, RaisedAtUtc, ClearedAtUtc, AcknowledgedAtUtc, AcknowledgedBy)
OUTPUT inserted.AlarmId VALUES (@equipmentId, @cause, @raisedAtUtc, @clearedAtUtc, @acknowledgedAtUtc, @acknowledgedBy)",
                parameters);
        }

        private int InsertFault(int alarmId, FaultStatus status, DateTime? recoveredAtUtc = null)
        {
            var parameters = new DynamicParameters(new { alarmId, status = (byte)status });
            parameters.Add("registeredAtUtc", Now.AddMinutes(1), DbType.DateTime2);
            parameters.Add("recoveredAtUtc", recoveredAtUtc, DbType.DateTime2);
            parameters.Add("closedAtUtc", recoveredAtUtc.HasValue ? Now.AddMinutes(2) : (DateTime?)null, DbType.DateTime2);
            return connection.QuerySingle<int>(@"
INSERT INTO dbo.FAULT (AlarmId, Status, RegisteredAtUtc, CauseCategory, CauseText, ActionText, RecoveredAtUtc, ClosedAtUtc)
OUTPUT inserted.FaultId
VALUES (@alarmId, @status, @registeredAtUtc, 1, N'cause text', N'action text', @recoveredAtUtc, @closedAtUtc)", parameters);
        }

        private void InsertHistory(int faultId, int userId, DateTime changedAtUtc,
            FaultStatus previousStatus, FaultStatus newStatus, string reason)
        {
            var parameters = new DynamicParameters(new
            {
                faultId, userId, previousStatus = (byte)previousStatus, newStatus = (byte)newStatus, reason
            });
            parameters.Add("changedAtUtc", changedAtUtc, DbType.DateTime2);
            connection.Execute(@"
INSERT INTO dbo.FAULT_HISTORY (FaultId, ChangedByUserId, ChangedAtUtc, PreviousStatus, NewStatus, Reason)
VALUES (@faultId, @userId, @changedAtUtc, @previousStatus, @newStatus, @reason)", parameters);
        }

        private static void AssertUtc(DateTime expected, DateTime actual)
        {
            Assert.Equal(expected, actual);
            Assert.Equal(DateTimeKind.Utc, actual.Kind);
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
