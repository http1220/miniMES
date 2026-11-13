using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.SqlClient;

namespace Server
{
    public sealed class ServerSettings
    {
        public string ServerUrl { get; private set; }
        public string ConnectionString { get; private set; }
        public string CollectorKey { get; private set; }
        public string DashboardKey { get; private set; }
        public int ProbeTimeoutSeconds { get; private set; }
        public int ProbeIntervalSeconds { get; private set; }
        public int CollectorStoppedSeconds { get; private set; }
        public string LogDirectory { get; private set; }

        public static IDictionary<string, string> ParseEnv(IEnumerable<string> lines)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                var text = line.Trim();
                if (text.Length == 0 || text.StartsWith("#", StringComparison.Ordinal)) continue;
                var separator = text.IndexOf('=');
                if (separator < 0) continue;
                values[text.Substring(0, separator).Trim()] = text.Substring(separator + 1).Trim();
            }
            return values;
        }

        public static IList<string> Validate(IDictionary<string, string> values,
            out ServerSettings settings, bool migrationOnly = false)
        {
            var errors = new List<string>();
            var connection = Required(values, "DB_CONNECTION_STRING", errors);
            if (connection != null)
            {
                try { new SqlConnectionStringBuilder(connection); }
                catch (Exception) { errors.Add("DB_CONNECTION_STRING must be a valid SQL connection string."); }
            }
            settings = new ServerSettings
            {
                ConnectionString = connection,
                LogDirectory = ResolveLogDirectory(values, AppDomain.CurrentDomain.BaseDirectory)
            };
            if (migrationOnly) return errors;
            settings.ServerUrl = Required(values, "SERVER_URL", errors);
            if (settings.ServerUrl != null && !settings.ServerUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                errors.Add("SERVER_URL must start with https://.");
            settings.CollectorKey = Required(values, "COLLECTOR_API_KEY", errors);
            settings.DashboardKey = Required(values, "DASHBOARD_API_KEY", errors);
            if (settings.CollectorKey != null && settings.CollectorKey.Length < 32)
                errors.Add("COLLECTOR_API_KEY must contain at least 32 characters.");
            if (settings.DashboardKey != null && settings.DashboardKey.Length < 32)
                errors.Add("DASHBOARD_API_KEY must contain at least 32 characters.");
            if (settings.CollectorKey != null && settings.DashboardKey != null &&
                string.Equals(settings.CollectorKey, settings.DashboardKey, StringComparison.Ordinal))
                errors.Add("DASHBOARD_API_KEY must differ from COLLECTOR_API_KEY.");
            settings.ProbeTimeoutSeconds = Positive(values, "DB_PROBE_TIMEOUT_SECONDS", 2, errors);
            settings.ProbeIntervalSeconds = Positive(values, "DB_PROBE_INTERVAL_SECONDS", 2, errors);
            settings.CollectorStoppedSeconds = Positive(values, "COLLECTOR_STOPPED_SECONDS", 10, errors);
            return errors;
        }

        public static string ResolveLogDirectory(IDictionary<string, string> values, string baseDirectory)
        {
            string value;
            if (!values.TryGetValue("LOG_DIRECTORY", out value) || string.IsNullOrWhiteSpace(value))
                return Path.Combine(baseDirectory, "logs");
            return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(baseDirectory, value));
        }

        private static string Required(IDictionary<string, string> values, string key, List<string> errors)
        {
            string value;
            if (values.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value)) return value;
            errors.Add(key + " is required.");
            return null;
        }

        private static int Positive(IDictionary<string, string> values, string key, int fallback, List<string> errors)
        {
            string value;
            if (!values.TryGetValue(key, out value)) return fallback;
            int number;
            if (int.TryParse(value, out number) && number >= 1) return number;
            errors.Add(key + " must be an integer greater than zero.");
            return fallback;
        }
    }
}
