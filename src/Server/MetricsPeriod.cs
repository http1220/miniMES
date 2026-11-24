using System;
using System.Globalization;

namespace Server
{
    public static class MetricsPeriod
    {
        public static bool TryParse(string fromText, string toText, DateTime nowUtc,
            out DateTime fromUtc, out DateTime toUtc)
        {
            fromUtc = default(DateTime);
            toUtc = default(DateTime);
            if (!TryUtc(fromText, out fromUtc) || !TryUtc(toText, out toUtc))
                return false;
            if (fromUtc >= toUtc)
                return false;
            if (toUtc - fromUtc > TimeSpan.FromDays(14))
                return false;
            return toUtc <= TruncateMilliseconds(nowUtc);
        }

        internal static DateTime TruncateMilliseconds(DateTime utc)
        {
            return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        }

        private static bool TryUtc(string text, out DateTime value)
        {
            value = default(DateTime);
            return text != null && text.Length == 24 &&
                DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value);
        }
    }
}
