using System;
using System.Data;
using Dapper;

namespace Data
{
    public sealed class CollectorRunRepository
    {
        private readonly IDbConnection connection;

        // The caller owns and disposes the connection.
        public CollectorRunRepository(IDbConnection connection)
        {
            this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        public void EnsureRun(Guid runId, DateTime startedAtUtc)
        {
            EnsureRun(runId, startedAtUtc, null);
        }

        // A supplied transaction remains under caller control, including on failure.
        public void EnsureRun(Guid runId, DateTime startedAtUtc, IDbTransaction transaction)
        {
            BatchTransaction.Execute(connection, transaction, current =>
            {
                var parameters = new DynamicParameters(new { runId });
                parameters.Add("startedAtUtc", startedAtUtc, DbType.DateTime2);
                return connection.Execute(@"
INSERT INTO dbo.COLLECTOR_RUN (RunId, StartedAtUtc)
SELECT @runId, @startedAtUtc
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.COLLECTOR_RUN WITH (UPDLOCK, HOLDLOCK)
    WHERE RunId = @runId)", parameters, current);
            });
        }
    }
}
