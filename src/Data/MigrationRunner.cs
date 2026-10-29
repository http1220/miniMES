using System;
using DbUp;

namespace Data
{
    public static class MigrationRunner
    {
        public static void Run(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("A database connection string is required.", nameof(connectionString));
            }

            var upgrader = DeployChanges.To
                .SqlDatabase(connectionString)
                .WithScriptsEmbeddedInAssembly(
                    typeof(MigrationRunner).Assembly,
                    name => name.StartsWith("Data.Migrations.", StringComparison.Ordinal)
                        && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                .WithTransaction()
                .LogToConsole()
                .Build();

            var result = upgrader.PerformUpgrade();
            if (!result.Successful)
            {
                throw new InvalidOperationException("Database migration failed.", result.Error);
            }
        }
    }
}
