using System;
using System.Threading;
using System.Threading.Tasks;
using Core;
using Serilog.Events;

namespace Collector
{
    public sealed class EquipmentLoop
    {
        private readonly EquipmentSettings equipment;
        private readonly CollectorSettings settings;
        private readonly CollectorState state;
        private readonly IModbusReader reader;
        private readonly Func<TimeSpan> now;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private readonly CollectorLog log;
        private readonly Func<double> random;
        private readonly ConnectionBackoff backoff;
        private readonly ReceiveDelayTracker receiveDelay;

        public EquipmentLoop(EquipmentSettings equipment, CollectorSettings settings, CollectorState state,
            IModbusReader reader, Func<TimeSpan> now, Func<TimeSpan, CancellationToken, Task> delay,
            CollectorLog log, Func<double> random)
            : this(equipment, settings, state, reader, now, delay, log, random, null)
        {
        }

        internal EquipmentLoop(EquipmentSettings equipment, CollectorSettings settings, CollectorState state,
            IModbusReader reader, Func<TimeSpan> now, Func<TimeSpan, CancellationToken, Task> delay,
            CollectorLog log, Func<double> random, ReceiveDelayTracker receiveDelay)
        {
            this.equipment = equipment;
            this.settings = settings;
            this.state = state;
            this.reader = reader;
            this.now = now;
            this.delay = delay;
            this.log = log;
            this.random = random;
            this.receiveDelay = receiveDelay;
            backoff = new ConnectionBackoff(settings.BackoffMaximum);
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            long consecutiveFailures = 0;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var cycleStart = now();
                    var result = await reader.ReadHoldingRegistersAsync(equipment.UnitId, equipment.StartAddress,
                        5, settings.ReadTimeout, cancellationToken);
                    if (result.Data != null)
                    {
                        var receivedAt = result.ReceivedAt ?? now();
                        ProcessSample(result.Data, receivedAt);
                        receiveDelay?.Record(now() - receivedAt);
                        backoff.OnConnectionSucceeded();

                        if (consecutiveFailures > 0)
                        {
                            log.Write(LogEventLevel.Information, CollectorEventKind.PollRecovered,
                                equipment.Id, count: consecutiveFailures);
                            consecutiveFailures = 0;
                        }
                    }
                    else
                    {
                        var failure = result.FailureType.Value;
                        state.OnFailedResponse(equipment.Id);
                        consecutiveFailures++;
                        log.Write(LogEventLevel.Warning, CollectorEventKind.PollFailed, equipment.Id,
                            failureType: failure.ToString());
                        if (ModbusRetryPolicy.ShouldReconnect(failure))
                        {
                            await delay(DelayJitter.ApplyJitter(backoff.OnConnectionFailed(), random(),
                                settings.BackoffMaximumJitterFraction), cancellationToken);
                            continue;
                        }
                    }

                    var remaining = cycleStart + settings.PollInterval - now();
                    await delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Other exceptions propagate to the runtime for supervision.
            }
        }

        private void ProcessSample(byte[] data, TimeSpan receivedAt)
        {
            var status = ReadRegister(data, 4);
            var temperature = SensorValueValidator.ValidateSample(
                RegisterFloatDecoder.Decode(ReadRegister(data, 0), ReadRegister(data, 1), equipment.WordOrder),
                equipment.TemperatureMinimum, equipment.TemperatureMaximum, status);
            var vibration = SensorValueValidator.ValidateSample(
                RegisterFloatDecoder.Decode(ReadRegister(data, 2), ReadRegister(data, 3), equipment.WordOrder),
                equipment.VibrationMinimum, equipment.VibrationMaximum, status);
            state.AddSample(equipment.Id, receivedAt, temperature.Measurement, vibration.Measurement,
                (byte?)temperature.StatusCode);

            AlarmSubmissionLog.Write(log, equipment.Id, AlarmCause.Communication,
                state.OnValidResponseAndSubmit(equipment.Id, receivedAt));
            if (temperature.Measurement.HasValue)
                AlarmSubmissionLog.Write(log, equipment.Id, AlarmCause.Temperature,
                    state.UpdateTemperatureAndSubmit(equipment.Id, temperature.Measurement.Value,
                        equipment.TemperatureUpperLimit, receivedAt));
            if (vibration.Measurement.HasValue)
                AlarmSubmissionLog.Write(log, equipment.Id, AlarmCause.Vibration,
                    state.UpdateVibrationAndSubmit(equipment.Id, vibration.Measurement.Value,
                        equipment.VibrationUpperLimit, receivedAt));
        }

        private static ushort ReadRegister(byte[] data, int register)
        {
            int offset = register * 2;
            return (ushort)((data[offset] << 8) | data[offset + 1]);
        }
    }

    internal static class AlarmSubmissionLog
    {
        internal static void Write(CollectorLog log, int equipmentId, AlarmCause cause, SubmitResult result)
        {
            var level = LogEventLevel.Information;
            CollectorEventKind kind;
            switch (result.Outcome)
            {
                case SubmitOutcome.Queued:
                    switch (result.Item.EventType)
                    {
                        case AlarmEventType.Raised: kind = CollectorEventKind.AlarmRaised; break;
                        case AlarmEventType.Cleared: kind = CollectorEventKind.AlarmCleared; break;
                        default: kind = CollectorEventKind.StatusReported; break;
                    }
                    break;
                case SubmitOutcome.HeldUnsent:
                    level = LogEventLevel.Warning;
                    kind = CollectorEventKind.AlarmUnsentHeld;
                    break;
                case SubmitOutcome.CancelledUnsentPair:
                    kind = CollectorEventKind.AlarmUnsentCancelled;
                    break;
                default:
                    return;
            }

            string detail;
            switch (cause)
            {
                case AlarmCause.Communication: detail = "Communication"; break;
                case AlarmCause.Temperature: detail = "Temperature"; break;
                case AlarmCause.Vibration: detail = "Vibration"; break;
                default: throw new ArgumentOutOfRangeException(nameof(cause));
            }
            log.Write(level, kind, equipmentId, count: result.Item?.EventSequence, detail: detail);
        }
    }
}
