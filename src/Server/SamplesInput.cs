using System;
using System.Collections.Generic;
using Data;
using Newtonsoft.Json.Linq;

namespace Server
{
    public sealed class SamplesInput
    {
        public Guid RunId { get; private set; }
        public DateTime StartedAtUtc { get; private set; }
        public IReadOnlyList<SampleRecord> Samples { get; private set; }

        public static string Validate(JObject body, out SamplesInput input)
        {
            input = null;
            Guid runId;
            DateTime started;
            if (body == null || !CollectorInput.TryRunId(body["runId"], out runId) ||
                !CollectorInput.TryUtc(body["startedAtUtc"], out started)) return "invalid_field";
            JArray items;
            var error = CollectorInput.CheckBatch(body["samples"], out items);
            if (error != null) return error;
            var samples = new List<SampleRecord>();
            foreach (var token in items)
            {
                var item = token as JObject;
                long sequence, equipment;
                DateTime collected;
                float? temperature, vibration;
                byte? status;
                if (item == null ||
                    !CollectorInput.TryInteger(item["sequenceNumber"], 1, long.MaxValue, out sequence) ||
                    !CollectorInput.TryInteger(item["equipmentId"], 1, int.MaxValue, out equipment) ||
                    !CollectorInput.TryUtc(item["collectedAtUtc"], out collected) ||
                    !TryMeasurement(item["temperature"], out temperature) ||
                    !TryMeasurement(item["vibration"], out vibration) ||
                    !TryStatus(item["statusCode"], out status)) return "invalid_field";
                samples.Add(new SampleRecord { RunId = runId, SequenceNumber = sequence,
                    EquipmentId = (int)equipment, CollectedAtUtc = collected,
                    Temperature = temperature, Vibration = vibration, StatusCode = status });
            }
            input = new SamplesInput { RunId = runId, StartedAtUtc = started, Samples = samples.AsReadOnly() };
            return null;
        }

        private static bool TryMeasurement(JToken token, out float? value)
        {
            value = null;
            if (token == null) return false;
            if (token.Type == JTokenType.Null) return true;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) return false;
            double number;
            try { number = (double)token; }
            catch (OverflowException) { return false; }
            if (double.IsNaN(number) || double.IsInfinity(number) || Math.Abs(number) > float.MaxValue) return false;
            value = (float)number;
            return true;
        }

        private static bool TryStatus(JToken token, out byte? value)
        {
            value = null;
            if (token == null) return false;
            if (token.Type == JTokenType.Null) return true;
            long number;
            if (!CollectorInput.TryInteger(token, 0, 255, out number)) return false;
            value = (byte)number;
            return true;
        }
    }
}
