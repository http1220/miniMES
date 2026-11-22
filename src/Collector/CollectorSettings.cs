using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using Core;

namespace Collector
{
    public sealed class CollectorSettings
    {
        private const NumberStyles IntegerStyle = NumberStyles.AllowLeadingSign;
        private const NumberStyles RealStyle = IntegerStyle | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
        private readonly EquipmentSettings[] equipment;

        public TimeSpan PollInterval { get; }
        public TimeSpan ReadTimeout { get; }
        public TimeSpan JudgeInterval { get; }
        public TimeSpan CommunicationFault { get; }
        public int CommunicationRecoverySuccessCount { get; }
        public int ThresholdConsecutiveCount { get; }
        public TimeSpan BackoffMaximum { get; }
        public double BackoffMaximumJitterFraction { get; }
        public int SampleBufferCapacity { get; }
        public int AlarmQueueCapacity { get; }
        public string LogDirectory { get; }
        public EquipmentSettings[] Equipment => (EquipmentSettings[])equipment.Clone();

        private CollectorSettings(int poll, int read, int judge, int fault, int recovery, int consecutive,
            int backoff, decimal jitter, int samples, int alarms, string logDirectory, EquipmentSettings[] equipment)
        {
            PollInterval = TimeSpan.FromMilliseconds(poll);
            ReadTimeout = TimeSpan.FromMilliseconds(read);
            JudgeInterval = TimeSpan.FromMilliseconds(judge);
            CommunicationFault = TimeSpan.FromMilliseconds(fault);
            CommunicationRecoverySuccessCount = recovery;
            ThresholdConsecutiveCount = consecutive;
            BackoffMaximum = TimeSpan.FromMilliseconds(backoff);
            BackoffMaximumJitterFraction = (double)jitter;
            SampleBufferCapacity = samples;
            AlarmQueueCapacity = alarms;
            LogDirectory = logDirectory;
            this.equipment = equipment;
        }

        public static CollectorSettings Load()
        {
            var source = ConfigurationManager.AppSettings;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in source.AllKeys)
                values.Add(key, source[key]);

            var errors = Validate(values, out var settings);
            if (errors.Count != 0)
                throw new ConfigurationErrorsException(string.Join(Environment.NewLine, errors));
            return settings;
        }

        public static IList<string> Validate(IDictionary<string, string> values, out CollectorSettings settings)
        {
            var errors = new List<string>();
            var poll = Integer(values, errors, "PollIntervalMilliseconds", 1000, 1);
            var read = Integer(values, errors, "ReadTimeoutMilliseconds", 500, 1);
            var judge = Integer(values, errors, "JudgeIntervalMilliseconds", 1000, 1);
            var fault = Integer(values, errors, "CommunicationFaultMilliseconds", 5000, 1);
            var recovery = Integer(values, errors, "CommunicationRecoverySuccessCount", 3, 1);
            var consecutive = Integer(values, errors, "ThresholdConsecutiveCount", 3, 1);
            var backoff = Integer(values, errors, "BackoffMaximumMilliseconds", 10000, 1000);
            var samples = Integer(values, errors, "SampleBufferCapacity", 3000, 1);
            var alarms = Integer(values, errors, "AlarmQueueCapacity", 3000, 1);
            var jitter = Jitter(values, errors);
            if (read >= poll)
                errors.Add("ReadTimeoutMilliseconds must be less than PollIntervalMilliseconds.");
            if (poll >= fault)
                errors.Add("PollIntervalMilliseconds must be less than CommunicationFaultMilliseconds.");
            if (backoff.HasValue && jitter.HasValue && poll.HasValue &&
                (jitter.Value > 15m || backoff.Value * (1m + jitter.Value) + poll.Value > 15000m))
                errors.Add("BackoffMaximumMilliseconds × (1 + BackoffMaximumJitterFraction) + PollIntervalMilliseconds must be at most 15000.");

            var equipment = new EquipmentSettings[5];
            var endpoints = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < equipment.Length; i++)
                equipment[i] = ReadEquipment(values, errors, endpoints, i + 1);

            var logDirectory = Get(values, "LogDirectory", "");
            if (string.IsNullOrWhiteSpace(logDirectory))
                logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

            settings = errors.Count == 0
                ? new CollectorSettings(poll.Value, read.Value, judge.Value, fault.Value, recovery.Value,
                    consecutive.Value, backoff.Value, jitter.Value, samples.Value, alarms.Value, logDirectory, equipment)
                : null;
            return errors;
        }

        private static EquipmentSettings ReadEquipment(IDictionary<string, string> values, List<string> errors,
            Dictionary<string, HashSet<int>> endpoints, int id)
        {
            var initialErrorCount = errors.Count;
            var prefix = "Equipment." + id.ToString(CultureInfo.InvariantCulture) + ".";
            var host = Get(values, prefix + "Host", "127.0.0.1")?.Trim();
            var port = Integer(values, errors, prefix + "Port", 15020 + id, 1, 65535);
            var unit = Integer(values, errors, prefix + "UnitId", 1, 0, 255);
            var start = Integer(values, errors, prefix + "StartAddress", (id - 1) * 100, 0, 65531);
            var order = Get(values, prefix + "WordOrder", id % 2 == 1 ? "ABCD" : "CDAB");
            if (order != "ABCD" && order != "CDAB")
                errors.Add(prefix + "WordOrder must be ABCD or CDAB.");
            if (string.IsNullOrEmpty(host))
                errors.Add(prefix + "Host must be nonempty.");
            else if (port.HasValue)
            {
                if (!endpoints.TryGetValue(host, out var ports))
                {
                    ports = new HashSet<int>();
                    endpoints.Add(host, ports);
                }
                if (!ports.Add(port.Value))
                    errors.Add(prefix + "Port duplicates another port on the same Host.");
            }

            var tempLimit = Number(values, errors, prefix + "TemperatureUpperLimit", 80);
            var vibLimit = Number(values, errors, prefix + "VibrationUpperLimit", 10);
            var tempMin = Number(values, errors, prefix + "TemperatureMinimum", -40);
            var tempMax = Number(values, errors, prefix + "TemperatureMaximum", 200);
            var vibMin = Number(values, errors, prefix + "VibrationMinimum", 0);
            var vibMax = Number(values, errors, prefix + "VibrationMaximum", 100);
            CheckRange(errors, prefix + "Temperature", tempMin, tempMax, tempLimit);
            CheckRange(errors, prefix + "Vibration", vibMin, vibMax, vibLimit);

            return errors.Count == initialErrorCount
                ? new EquipmentSettings(id, host, port.Value, (byte)unit.Value, (ushort)start.Value,
                    order == "ABCD" ? RegisterWordOrder.ABCD : RegisterWordOrder.CDAB,
                    tempLimit.Value, vibLimit.Value, tempMin.Value, tempMax.Value, vibMin.Value, vibMax.Value)
                : null;
        }

        private static void CheckRange(List<string> errors, string key, float? min, float? max, float? limit)
        {
            if (min >= max)
                errors.Add(key + "Minimum must be less than " + key + "Maximum.");
            else if (limit < min || limit > max)
                errors.Add(key + "UpperLimit must be within the valid range.");
        }

        private static string Get(IDictionary<string, string> values, string key, string fallback)
        {
            return values.TryGetValue(key, out var value) ? value : fallback;
        }

        private static bool IsNumericText(string text)
        {
            return !string.IsNullOrEmpty(text) && text == text.Trim() && text.IndexOf('\0') < 0;
        }

        private static int? Integer(IDictionary<string, string> values, List<string> errors,
            string key, int fallback, int minimum, int maximum = int.MaxValue)
        {
            if (!values.TryGetValue(key, out var text))
                return fallback;
            if (IsNumericText(text) && int.TryParse(text, IntegerStyle, CultureInfo.InvariantCulture, out var result)
                && result >= minimum && result <= maximum)
                return result;
            errors.Add(FormattableString.Invariant($"{key} must be an integer in {minimum}..{maximum}."));
            return null;
        }

        private static decimal? Jitter(IDictionary<string, string> values, List<string> errors)
        {
            const string key = "BackoffMaximumJitterFraction";
            var text = Get(values, key, "0.2");
            if (IsNumericText(text) && decimal.TryParse(text, RealStyle, CultureInfo.InvariantCulture, out var result)
                && result >= 0m)
                return result;
            errors.Add(key + " must be a finite, nonnegative decimal number.");
            return null;
        }

        private static float? Number(IDictionary<string, string> values, List<string> errors, string key, float fallback)
        {
            if (!values.TryGetValue(key, out var text))
                return fallback;
            if (IsNumericText(text) && float.TryParse(text, RealStyle, CultureInfo.InvariantCulture, out var result)
                && !float.IsNaN(result) && !float.IsInfinity(result))
                return result;
            errors.Add(key + " must be a finite float32 number.");
            return null;
        }
    }

    public sealed class EquipmentSettings
    {
        public int Id { get; }
        public string Host { get; }
        public int Port { get; }
        public byte UnitId { get; }
        public ushort StartAddress { get; }
        public RegisterWordOrder WordOrder { get; }
        public float TemperatureUpperLimit { get; }
        public float VibrationUpperLimit { get; }
        public float TemperatureMinimum { get; }
        public float TemperatureMaximum { get; }
        public float VibrationMinimum { get; }
        public float VibrationMaximum { get; }

        internal EquipmentSettings(int id, string host, int port, byte unitId, ushort startAddress,
            RegisterWordOrder wordOrder, float temperatureUpperLimit, float vibrationUpperLimit,
            float temperatureMinimum, float temperatureMaximum, float vibrationMinimum, float vibrationMaximum)
        {
            Id = id;
            Host = host;
            Port = port;
            UnitId = unitId;
            StartAddress = startAddress;
            WordOrder = wordOrder;
            TemperatureUpperLimit = temperatureUpperLimit;
            VibrationUpperLimit = vibrationUpperLimit;
            TemperatureMinimum = temperatureMinimum;
            TemperatureMaximum = temperatureMaximum;
            VibrationMinimum = vibrationMinimum;
            VibrationMaximum = vibrationMaximum;
        }
    }
}
