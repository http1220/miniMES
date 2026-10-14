using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Simulator
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            using (var shutdown = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler cancel = (sender, e) =>
                {
                    e.Cancel = true;
                    shutdown.Cancel();
                };
                Console.CancelKeyPress += cancel;
                Equipment[] equipment = null;
                try
                {
                    if (args.Length != 0)
                        throw new ArgumentException("Usage: Simulator.exe (configuration: Simulator.exe.config)");
                    equipment = SimulatorSettings.Load().Select(settings => new Equipment(settings)).ToArray();
                    foreach (var machine in equipment)
                        machine.Start();
                    var commands = new CommandProcessor(equipment);
                    Console.WriteLine("READY");
                    Console.Out.Flush();
                    var stopped = Task.Delay(Timeout.Infinite, shutdown.Token);
                    while (!shutdown.IsCancellationRequested)
                    {
                        // A background read lets Ctrl+C interrupt redirected input as well.
                        var input = Task.Run(() => Console.ReadLine());
                        if (Task.WhenAny(input, stopped).GetAwaiter().GetResult() != input)
                            break;
                        var line = input.GetAwaiter().GetResult();
                        if (line == null) break;
                        bool quit;
                        var response = commands.Execute(line, out quit);
                        if (quit)
                            foreach (var machine in equipment)
                                machine.Dispose();
                        Console.WriteLine(response);
                        Console.Out.Flush();
                        if (quit) break;
                    }
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Simulator failed: " + ex.Message);
                    return 1;
                }
                finally
                {
                    if (equipment != null)
                        foreach (var machine in equipment)
                            machine.Dispose();
                    Console.CancelKeyPress -= cancel;
                }
            }
        }
    }
}
