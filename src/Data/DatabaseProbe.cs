using System;
using Microsoft.Data.SqlClient;

namespace Data
{
    public sealed class DatabaseProbe
    {
        private readonly string connectionString;
        private readonly int timeoutSeconds;

        public DatabaseProbe(string connectionString, int timeoutSeconds)
        {
            if (timeoutSeconds < 1) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            this.connectionString = connectionString;
            this.timeoutSeconds = timeoutSeconds;
        }

        public bool Check()
        {
            int? ignored;
            return Check(out ignored);
        }

        public bool Check(out int? sqlErrorNumber)
        {
            sqlErrorNumber = null;
            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = timeoutSeconds, Pooling = false, ConnectRetryCount = 0 };
                using (var connection = new SqlConnection(builder.ConnectionString))
                using (var command = new SqlCommand("SELECT 1", connection) { CommandTimeout = timeoutSeconds })
                {
                    connection.Open();
                    return Equals(command.ExecuteScalar(), 1);
                }
            }
            catch (SqlException exception) { sqlErrorNumber = exception.Number; return false; }
            catch (InvalidOperationException) { return false; }
            catch (TimeoutException) { return false; }
            catch (ArgumentException) { return false; }
        }
    }
}
