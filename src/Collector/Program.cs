using System;
using System.Configuration;
using System.Linq;
using System.Threading;
using Serilog.Events;

namespace Collector
{
    class Program
    {
        static int Main(string[] args)
        {
            CollectorSettings settings;
            try
            {
                settings = CollectorSettings.Load();
            }
            catch (ConfigurationErrorsException error)
            {
                Console.Error.WriteLine(string.IsNullOrEmpty(error.Filename)
                    ? error.BareMessage
                    : "Collector configuration could not be read.");
                return 1;
            }

            var runId = Guid.NewGuid();
            using (var log = new CollectorLog(settings.LogDirectory, runId))
            using (var cancellation = new CancellationTokenSource())
            {
                log.Write(LogEventLevel.Information, CollectorEventKind.Started);
                var state = new CollectorState(runId, settings.Equipment.Select(device => device.Id),
                    settings.SampleBufferCapacity, settings.AlarmQueueCapacity,
                    settings.CommunicationRecoverySuccessCount, settings.ThresholdConsecutiveCount);
                var time = CollectorTime.StartReal();
                var runtime = new CollectorRuntime(settings, state, log,
                    device => new ModbusTcpClientReader(device.Host, device.Port, time.Now),
                    time.Now, time.Delay, id => new Random(unchecked(Environment.TickCount + id)).NextDouble);

                ConsoleCancelEventHandler cancel = (sender, signal) =>
                {
                    signal.Cancel = true;
                    cancellation.Cancel();
                };
                Console.CancelKeyPress += cancel;
                try
                {
                    runtime.RunAsync(cancellation.Token).GetAwaiter().GetResult();
                }
                finally
                {
                    Console.CancelKeyPress -= cancel;
                }
            }
            return 0;
        }
    }
}