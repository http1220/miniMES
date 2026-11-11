using System;
using System.Data;
using System.Data.Common;
using System.Threading;

namespace IntegrationTests
{
    // Widen the read/insert window without synchronizing or serializing the workers.
    internal sealed class DelayedFaultReadConnection : IDbConnection
    {
        private readonly IDbConnection inner;
        private int emptyActiveReads;

        internal int EmptyActiveReads => Volatile.Read(ref emptyActiveReads);

        internal DelayedFaultReadConnection(IDbConnection inner)
        {
            this.inner = inner;
        }

        public string ConnectionString { get => inner.ConnectionString; set => inner.ConnectionString = value; }
        public int ConnectionTimeout => inner.ConnectionTimeout;
        public string Database => inner.Database;
        public ConnectionState State => inner.State;
        public IDbTransaction BeginTransaction() => inner.BeginTransaction();
        public IDbTransaction BeginTransaction(IsolationLevel isolation) => inner.BeginTransaction(isolation);
        public void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);
        public void Close() => inner.Close();
        public void Open() => inner.Open();
        public void Dispose() => inner.Dispose();
        public IDbCommand CreateCommand() => new DelayedCommand(inner.CreateCommand(), this);

        private sealed class DelayedCommand : IDbCommand
        {
            private readonly IDbCommand inner;
            private readonly DelayedFaultReadConnection observer;

            internal DelayedCommand(IDbCommand inner, DelayedFaultReadConnection observer)
            {
                this.inner = inner;
                this.observer = observer;
            }

            public string CommandText { get => inner.CommandText; set => inner.CommandText = value; }
            public int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
            public CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
            public IDbConnection Connection { get => inner.Connection; set => inner.Connection = value; }
            public IDataParameterCollection Parameters => inner.Parameters;
            public IDbTransaction Transaction { get => inner.Transaction; set => inner.Transaction = value; }
            public UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
            public void Cancel() => inner.Cancel();
            public IDbDataParameter CreateParameter() => inner.CreateParameter();
            public int ExecuteNonQuery() => inner.ExecuteNonQuery();
            public object ExecuteScalar() => inner.ExecuteScalar();
            public void Prepare() => inner.Prepare();
            public void Dispose() => inner.Dispose();
            public IDataReader ExecuteReader() => ExecuteReader(CommandBehavior.Default);

            public IDataReader ExecuteReader(CommandBehavior behavior)
            {
                var activeLookup = inner.CommandText.StartsWith("SELECT FaultId FROM dbo.FAULT", StringComparison.Ordinal);
                if (activeLookup)
                {
                    // Dapper drains the next result before returning, after the SELECT has completed.
                    // Without range locks, both workers can finish the empty lookup during this delay.
                    inner.CommandText += "; WAITFOR DELAY '00:00:00.050';";
                }
                var reader = inner.ExecuteReader(behavior);
                if (activeLookup && !((DbDataReader)reader).HasRows)
                    Interlocked.Increment(ref observer.emptyActiveReads);
                return reader;
            }
        }
    }
}
