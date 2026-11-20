using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Formatting.Compact;
using Server;
using Xunit;

namespace IntegrationTests
{
    public sealed class ApiKeyMatrixTests
    {
        private const string CollectorKey = "fake-collector-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string DashboardKey = "fake-dashboard-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        [Fact]
        public async Task Authorization_CollectorAndDashboardEndpoints_EnforcesKeyMatrixAndHidesSecrets()
        {
            var connectionString = Environment.GetEnvironmentVariable("MINIMES_TEST_SQL_CONNECTION_STRING");
            Assert.False(string.IsNullOrWhiteSpace(connectionString));
            var path = Path.Combine(Path.GetTempPath(), "key-matrix-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                ServerSettings settings;
                Assert.Empty(ServerSettings.Validate(new Dictionary<string, string> { ["SERVER_URL"] = "https://+:5443/", ["DB_CONNECTION_STRING"] = connectionString, ["COLLECTOR_API_KEY"] = CollectorKey, ["DASHBOARD_API_KEY"] = DashboardKey }, out settings));
                using (var logger = new LoggerConfiguration().WriteTo.File(new CompactJsonFormatter(), path).CreateLogger())
                using (var database = new DatabaseMonitor(() => false, TimeSpan.FromSeconds(2)))
                using (var configuration = ApiConfiguration.Create(new ApiRuntime(settings, new CollectorState(() => TimeSpan.Zero, TimeSpan.FromSeconds(10)), database, () => DateTime.UtcNow, logger)))
                using (var client = new HttpClient(new HttpServer(configuration)) { BaseAddress = new Uri("http://localhost/") })
                {
                    foreach (var endpoint in new[] { "/api/v1/collector/alarm-events", "/api/v1/collector/samples" })
                        await Send(client, HttpMethod.Post, endpoint, "wrong-key", "{}", 401, "unauthorized");
                    var heartbeat = "{\"runId\":\"" + Guid.NewGuid() + "\"}";
                    await Send(client, HttpMethod.Post, "/api/v1/collector/heartbeat", null, heartbeat, 401, "unauthorized");
                    await Send(client, HttpMethod.Post, "/api/v1/collector/heartbeat", "wrong-key", heartbeat, 401, "unauthorized");
                    await Send(client, HttpMethod.Post, "/api/v1/collector/heartbeat", DashboardKey, heartbeat, 403, "forbidden");
                    await Send(client, HttpMethod.Post, "/api/v1/collector/heartbeat", CollectorKey, heartbeat, 200, null);
                    await Send(client, HttpMethod.Get, "/api/v1/status", null, null, 401, "unauthorized");
                    await Send(client, HttpMethod.Get, "/api/v1/status", "wrong-key", null, 401, "unauthorized");
                    await Send(client, HttpMethod.Get, "/api/v1/status", CollectorKey, null, 403, "forbidden");
                    await Send(client, HttpMethod.Get, "/api/v1/status", DashboardKey, null, 200, null);
                }
                var logs = File.ReadAllText(path);
                Assert.DoesNotContain(CollectorKey, logs);
                Assert.DoesNotContain(DashboardKey, logs);
                Assert.DoesNotContain(connectionString, logs);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        private static async Task Send(HttpClient client, HttpMethod method, string path, string key, string body, int status, string error)
        {
            using (var request = new HttpRequestMessage(method, path))
            {
                if (key != null) request.Headers.Add("X-Api-Key", key);
                if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (var response = await client.SendAsync(request))
                {
                    Assert.Equal(status, (int)response.StatusCode);
                    var text = await response.Content.ReadAsStringAsync();
                    Assert.DoesNotContain(CollectorKey, text);
                    Assert.DoesNotContain(DashboardKey, text);
                    if (error != null) Assert.Equal(error, (string)JObject.Parse(text)["error"]);
                }
            }
        }
    }
}
