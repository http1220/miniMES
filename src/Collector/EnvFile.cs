using System;
using System.Collections.Generic;

namespace Collector
{
    public static class EnvFile
    {
        public static IDictionary<string, string> Parse(IEnumerable<string> lines)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                var text = line.Trim();
                if (text.Length == 0 || text.StartsWith("#", StringComparison.Ordinal))
                    continue;
                var separator = text.IndexOf('=');
                if (separator < 0)
                    continue;
                values[text.Substring(0, separator).Trim()] = text.Substring(separator + 1).Trim();
            }
            return values;
        }
    }
}
