using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Collector;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog.Events;
using Xunit;

namespace UnitTests
{
    public class CollectorLogTests
    {
        [Fact]
        public void Write_NullFieldsAndException_RecordsOnlyStructuredMetadata()
        {
            var runId = Guid.NewGuid();
            const string secret = "secret-in-exception-payload";
            InTemporaryDirectory(directory =>
            {
                using (var log = new CollectorLog(directory, runId))
                {
                    log.Write(LogEventLevel.Information, CollectorEventKind.Started);
                    log.Write(LogEventLevel.Error, CollectorEventKind.ExceptionObserved, 2, "Read",
                        new InvalidOperationException(secret), 7, "Worker stopped");
                }

                var files = Directory.GetFiles(directory).OrderBy(path => path).ToArray();
                foreach (var file in files)
                {
                    Assert.Matches(@"^collector-[0-9]{8}\.log$", Path.GetFileName(file));
                    Assert.DoesNotContain(secret, File.ReadAllText(file));
                }
                var lines = files.SelectMany(File.ReadAllLines).ToArray();
                Assert.Equal(2, lines.Length);

                using (var reader = new JsonTextReader(new StringReader(lines[0])) { DateParseHandling = DateParseHandling.None })
                {
                    var started = JObject.Load(reader);
                    Assert.Equal("Started", (string)started["EventKind"]);
                    Assert.Equal(runId, (Guid)started["RunId"]);
                    foreach (var field in new[] { "EquipmentId", "FailureType", "ExceptionType", "Count", "Detail" })
                        Assert.Equal(JTokenType.Null, started[field].Type);
                    Assert.EndsWith("Z", (string)started["@t"]);
                }

                var failure = JObject.Parse(lines[1]);
                Assert.Equal("ExceptionObserved", (string)failure["EventKind"]);
                Assert.Equal(runId, (Guid)failure["RunId"]);
                Assert.Equal(2, (int)failure["EquipmentId"]);
                Assert.Equal("Read", (string)failure["FailureType"]);
                Assert.Equal(typeof(InvalidOperationException).FullName, (string)failure["ExceptionType"]);
                Assert.Equal(7L, (long)failure["Count"]);
                Assert.Equal("Worker stopped", (string)failure["Detail"]);
                Assert.Null(failure["@x"]);
            });
        }

        [Fact]
        public void Write_ConcurrentCalls_ProducesOneJsonObjectPerEvent()
        {
            const int count = 128;
            InTemporaryDirectory(directory =>
            {
                using (var log = new CollectorLog(directory, Guid.NewGuid()))
                {
                    Parallel.For(0, count,
                        i => log.Write(LogEventLevel.Warning, CollectorEventKind.PollFailed, count: i));
                }

                var seen = new bool[count];
                var lines = Directory.GetFiles(directory).SelectMany(File.ReadAllLines).ToArray();
                Assert.Equal(count, lines.Length);
                foreach (var line in lines)
                {
                    var entry = JObject.Parse(line);
                    var index = (int)entry["Count"];
                    Assert.False(seen[index]);
                    seen[index] = true;
                }
            });
        }

        private static void InTemporaryDirectory(Action<string> verify)
        {
            var path = Path.Combine(Path.GetTempPath(), "collector-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            try { verify(path); }
            finally { Directory.Delete(path, recursive: true); }
        }
    }
}
