using System;
using System.IO;
using Core;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Collector
{
    public enum CollectorEventKind
    {
        Started, ConfigurationInvalid, PollFailed, SampleStored, SampleBufferDropped,
        AlarmQueueRejected, AlarmUnsentHeld, AlarmUnsentCancelled, AlarmUnsentRequeued,
        AlarmRaised, AlarmCleared, StatusReported, ReceiveDelay, ExceptionObserved,
        PollRecovered, StatusSnapshot,
        TransmitFailed, TransmitRecovered, BatchDiscarded, ClockJump, HeartbeatFailed, HeartbeatRecovered,
        LoopFatal, ShutdownRequested, ShutdownCompleted, ShutdownIncomplete
    }

    public sealed class CollectorLog : IDisposable
    {
        private readonly Logger logger;
        private readonly Guid runId;

        public CollectorLog(string directory, Guid runId)
        {
            this.runId = runId;
            logger = new LoggerConfiguration().WriteTo.File(new CompactJsonFormatter(),
                Path.Combine(directory, "collector-.log"), rollingInterval: RollingInterval.Day).CreateLogger();
        }

        // Callers must pass only fixed text as detail.
        public void Write(LogEventLevel level, CollectorEventKind kind, int? equipmentId = null,
            string failureType = null, Exception exception = null, long? count = null, string detail = null)
        {
            logger.Write(level,
                "{EventKind} equipment={EquipmentId} run={RunId} failure={FailureType} exception={ExceptionType} count={Count} detail={Detail}",
                kind.ToString(), equipmentId, runId, failureType, exception?.GetType().FullName, count, detail);
        }

        internal static void WriteClockJump(CollectorLog log, ClockJump jump)
        {
            if (jump != null)
                log.Write(LogEventLevel.Warning, CollectorEventKind.ClockJump,
                    count: (long)jump.Delta.TotalMilliseconds,
                    detail: jump.Delta > TimeSpan.Zero ? "Forward" : "Backward");
        }

        public void Dispose()
        {
            logger.Dispose();
        }
    }
}
