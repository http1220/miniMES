using System;
using System.Diagnostics;
using System.IO;
using Data;
using Microsoft.Data.SqlClient;
using Microsoft.Owin.Hosting;
using Serilog;
using Serilog.Formatting.Compact;

namespace Server
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length > 1 || (args.Length == 1 && args[0] != "migrate"))
            {
                Console.Error.WriteLine("Usage: Server.exe [migrate]");
                return 2;
            }
            Serilog.Core.Logger logger = null;
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env");
                var values = ServerSettings.ParseEnv(File.Exists(path) ? File.ReadAllLines(path) : new string[0]);
                var directory = ServerSettings.ResolveLogDirectory(values, AppDomain.CurrentDomain.BaseDirectory);
                logger = new LoggerConfiguration().WriteTo.File(new CompactJsonFormatter(),
                    Path.Combine(directory, "server-.log"), rollingInterval: RollingInterval.Day).CreateLogger();
                ServerSettings settings;
                var errors = ServerSettings.Validate(values, out settings, args.Length == 1);
                if (errors.Count != 0)
                {
                    foreach (var error in errors)
                    {
                        Console.Error.WriteLine(error);
                        logger.Error("Configuration invalid {Violation}", error);
                    }
                    return 1;
                }
                if (args.Length == 1)
                {
                    var stdout = Console.Out;
                    var stderr = Console.Error;
                    try
                    {
                        Console.SetOut(TextWriter.Null);
                        Console.SetError(TextWriter.Null);
                        MigrationRunner.Run(settings.ConnectionString);
                    }
                    finally { Console.SetOut(stdout); Console.SetError(stderr); }
                    return 0;
                }
                var probe = new DatabaseProbe(settings.ConnectionString, settings.ProbeTimeoutSeconds);
                using (var database = new DatabaseMonitor(() => probe.Check(), TimeSpan.FromSeconds(settings.ProbeIntervalSeconds)))
                {
                    var stopwatch = Stopwatch.StartNew();
                    var runtime = new ApiRuntime(settings,
                        new CollectorState(() => stopwatch.Elapsed, TimeSpan.FromSeconds(settings.CollectorStoppedSeconds)),
                        database, () => DateTime.UtcNow, logger);
                    database.Start();
                    using (WebApp.Start(settings.ServerUrl, app => ApiConfiguration.Configure(app, runtime)))
                    {
                        Console.WriteLine("Press Enter to stop.");
                        Console.ReadLine();
                    }
                }
                return 0;
            }
            catch (Exception exception)
            {
                if (logger == null) Console.Error.WriteLine("Startup failed: " + exception.GetType().FullName);
                else
                {
                    for (var current = exception; current != null; current = current.InnerException)
                    {
                        var sql = current as SqlException;
                        logger.Error("Operation failed {ExceptionType} {SqlErrorNumber}", current.GetType().FullName,
                            sql == null ? (int?)null : sql.Number);
                    }
                }
                return 1;
            }
            finally { if (logger != null) logger.Dispose(); }
        }
    }
}