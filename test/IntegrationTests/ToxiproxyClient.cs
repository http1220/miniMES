using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace IntegrationTests
{
    internal sealed class ToxiproxyClient : IDisposable
    {
        private const string Help = " See docs/fault-injection.md.";
        private readonly string name;
        private readonly string proxyPath;
        private readonly HttpClient client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri("http://127.0.0.1:8474/"),
            Timeout = TimeSpan.FromSeconds(10)
        };

        public ToxiproxyClient(string name)
        {
            this.name = name;
            proxyPath = "proxies/" + Uri.EscapeDataString(name);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("minimes-tests");
        }

        public async Task CheckReady(string listen, string upstream)
        {
            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(proxyPath);
            }
            catch (HttpRequestException)
            {
                Assert.True(false, "Toxiproxy API not reachable at 127.0.0.1:8474." + Help);
                return;
            }
            catch (TaskCanceledException)
            {
                Assert.True(false, "Toxiproxy API not reachable at 127.0.0.1:8474 (timeout)." + Help);
                return;
            }

            using (response)
            {
                Assert.True(response.StatusCode != HttpStatusCode.NotFound, $"Proxy {name} is missing." + Help);
                Assert.True(response.IsSuccessStatusCode, $"Toxiproxy proxy lookup failed: {(int)response.StatusCode}." + Help);
                var proxy = JObject.Parse(await response.Content.ReadAsStringAsync());
                Assert.True((string)proxy["name"] == name, $"Expected proxy name {name}." + Help);
                Assert.True((string)proxy["listen"] == listen,
                    $"Unexpected listen: {(string)proxy["listen"]}; expected {listen}." + Help);
                Assert.True((string)proxy["upstream"] == upstream,
                    $"Unexpected upstream: {(string)proxy["upstream"]}; expected {upstream}." + Help);
                Assert.True(proxy["toxics"] is JArray toxics && !toxics.Any(),
                    $"Proxy {name} has leftover toxics; remove them before running the test." + Help);
                Assert.True((bool?)proxy["enabled"] == true, $"Proxy {name} is disabled; enable it before running the test." + Help);
            }
        }

        public Task AddToxic(string name, string type, string stream, JObject attributes)
        {
            return PostAsync(proxyPath + "/toxics", new JObject
            {
                ["name"] = name, ["type"] = type, ["stream"] = stream,
                ["toxicity"] = 1.0, ["attributes"] = attributes
            }, $"add toxic {name} ({type}: {attributes.ToString(Newtonsoft.Json.Formatting.None)})");
        }

        public async Task RemoveToxic(string name, bool ignoreNotFound)
        {
            using (var response = await client.DeleteAsync(proxyPath + "/toxics/" + Uri.EscapeDataString(name)))
            {
                if (!ignoreNotFound || response.StatusCode != HttpStatusCode.NotFound)
                    Assert.True(response.IsSuccessStatusCode, $"Could not remove {name}: HTTP {(int)response.StatusCode}." + Help);
            }
        }

        public Task SetEnabled(bool enabled)
        {
            return PostAsync(proxyPath, new JObject { ["enabled"] = enabled }, $"set enabled={enabled}");
        }

        private async Task PostAsync(string path, JObject body, string action)
        {
            using (var content = new StringContent(body.ToString(), Encoding.UTF8, "application/json"))
            using (var response = await client.PostAsync(path, content))
                Assert.True(response.IsSuccessStatusCode, $"Could not {action} on {name}: HTTP {(int)response.StatusCode}." + Help);
        }

        public void Dispose()
        {
            client.Dispose();
        }
    }
}
