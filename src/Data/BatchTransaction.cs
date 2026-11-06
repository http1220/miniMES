using System;
using System.Data;

namespace Data
{
    internal static class BatchTransaction
    {
        internal static T Execute<T>(IDbConnection connection, IDbTransaction transaction,
            Func<IDbTransaction, T> action)
        {
            if (transaction != null)
            {
                if (!ReferenceEquals(transaction.Connection, connection))
                    throw new ArgumentException("The transaction must belong to this connection.", nameof(transaction));
                return action(transaction);
            }

            var openedHere = connection.State == ConnectionState.Closed;
            try
            {
                if (openedHere)
                    connection.Open();
                // Disposal rolls back if execution or commit fails.
                using (var owned = connection.BeginTransaction())
                {
                    var result = action(owned);
                    owned.Commit();
                    return result;
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
