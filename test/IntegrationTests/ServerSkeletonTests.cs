using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Data;
using Microsoft.Data.SqlClient;
using Microsoft.Owin.Hosting;
using Serilog;
using Server;
using Xunit;

namespace IntegrationTests
{
    public sealed class ServerSkeletonTests
    {
        private static string ConnectionString()
        {
            var value = Environment.GetEnvironmentVariable("MINIMES_TEST_SQL_CONNECTION_STRING");
            Assert.False(string.IsNullOrWhiteSpace(value), "Set MINIMES_TEST_SQL_CONNECTION_STRING for a test database.");
            return value;
        }

        [Fact]
        public void DatabaseProbe_ConnectsToTestDatabase()
        {
            Assert.True(new DatabaseProbe(ConnectionString(), 2).Check());
        }

        [Fact]
        public void DatabaseProbe_UnreachableServerReturnsFalseWithinBound()
        {
            var builder = new SqlConnectionStringBuilder(ConnectionString())
            {
                DataSource = "tcp:127.0.0.1,1"
            };
            var stopwatch = Stopwatch.StartNew();
            Assert.False(new DatabaseProbe(builder.ConnectionString, 2).Check());
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void DatabaseProbe_MissingDatabaseReturnsFalse()
        {
            var builder = new SqlConnectionStringBuilder(ConnectionString())
            {
                InitialCatalog = "Missing_" + Guid.NewGuid().ToString("N")
            };
            Assert.False(new DatabaseProbe(builder.ConnectionString, 2).Check());
        }

        [Fact]
        [Trait("Category", "Https")]
        public async Task HttpsHost_RequiresDashboardKey()
        {
            const string dashboardKey = "test-dashboard-key-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            ServerSettings settings;
            Assert.Empty(ServerSettings.Validate(new System.Collections.Generic.Dictionary<string, string>
            {
                ["SERVER_URL"] = "https://+:5443/",
                ["DB_CONNECTION_STRING"] = ConnectionString(),
                ["COLLECTOR_API_KEY"] = "test-collector-key-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                ["DASHBOARD_API_KEY"] = dashboardKey
            }, out settings));
            var stopwatch = Stopwatch.StartNew();
            using (var logger = new LoggerConfiguration().CreateLogger())
            using (var database = new DatabaseMonitor(() => true, TimeSpan.FromSeconds(2)))
            {
                var runtime = new ApiRuntime(settings,
                    new CollectorState(() => stopwatch.Elapsed, TimeSpan.FromSeconds(10)), database, () => DateTime.UtcNow, logger);
                database.Start();
                using (WebApp.Start(settings.ServerUrl, app => ApiConfiguration.Configure(app, runtime)))
                using (var client = new HttpClient())
                {
                    var uri = "https://localhost:5443/api/v1/status";
                    using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                    {
                        request.Headers.Add("X-Api-Key", dashboardKey);
                        using (var response = await client.SendAsync(request)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    }
                    using (var response = await client.GetAsync(uri)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                }
            }
        }
    }
}
