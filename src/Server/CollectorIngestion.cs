using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using Data;
using Newtonsoft.Json.Linq;

namespace Server
{
    public static class CollectorInput
    {
        public static bool TryRunId(JToken token, out Guid value)
        {
            value = Guid.Empty;
            return token != null && token.Type == JTokenType.String &&
                Guid.TryParse((string)token, out value) && value != Guid.Empty;
        }

        public static bool TryUtc(JToken token, out DateTime value)
        {
            value = default(DateTime);
            return token != null && token.Type == JTokenType.String &&
                ((string)token).Length == 24 && DateTime.TryParseExact((string)token,
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value);
        }

        public static string CheckBatch(JToken token, out JArray items)
        {
            items = token as JArray;
            if (items == null || items.Count == 0) return "invalid_field";
            return items.Count > 25 ? "batch_too_large" : null;
        }

        public static bool TryInteger(JToken token, long minimum, long maximum, out long value)
        {
            value = 0;
            return token != null && token.Type == JTokenType.Integer &&
                long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
                value >= minimum && value <= maximum;
        }
    }

    public sealed class AlarmEventsInput
    {
        public Guid RunId { get; private set; }
        public DateTime StartedAtUtc { get; private set; }
        public IReadOnlyList<AlarmEventRecord> Events { get; private set; }

        public static string Validate(JObject body, out AlarmEventsInput input)
        {
            input = null;
            Guid runId;
            DateTime started;
            if (body == null || !CollectorInput.TryRunId(body["runId"], out runId) ||
                !CollectorInput.TryUtc(body["startedAtUtc"], out started)) return "invalid_field";
            JArray items;
            var error = CollectorInput.CheckBatch(body["events"], out items);
            if (error != null) return error;
            var events = new List<AlarmEventRecord>();
            foreach (var token in items)
            {
                var item = token as JObject;
                long sequence, equipment, cause, type;
                DateTime occurred;
                if (item == null ||
                    !CollectorInput.TryInteger(item["eventSequence"], 1, long.MaxValue, out sequence) ||
                    !CollectorInput.TryInteger(item["equipmentId"], 1, int.MaxValue, out equipment) ||
                    !CollectorInput.TryInteger(item["cause"], 1, 3, out cause) ||
                    !CollectorInput.TryInteger(item["eventType"], 1, 4, out type) ||
                    !CollectorInput.TryUtc(item["occurredAtUtc"], out occurred)) return "invalid_field";
                events.Add(new AlarmEventRecord { RunId = runId, EventSequence = sequence,
                    EquipmentId = (int)equipment, Cause = (byte)cause, EventType = (byte)type,
                    OccurredAtUtc = occurred });
            }
            input = new AlarmEventsInput { RunId = runId, StartedAtUtc = started, Events = events.AsReadOnly() };
            return null;
        }
    }

    public static class CollectorFailure
    {
        public static HttpStatusCode ClassifySql(int number)
        {
            return number == 547 || number == 2601 || number == 2627 || number == 8152
                ? HttpStatusCode.BadRequest : HttpStatusCode.ServiceUnavailable;
        }

        public static HttpStatusCode ClassifyNonSql(Exception exception)
        {
            return HttpStatusCode.InternalServerError;
        }
    }
}
