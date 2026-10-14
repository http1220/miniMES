using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.Net;

namespace Simulator
{
    internal enum WordOrder { ABCD, CDAB }

    internal sealed class EquipmentSettings
    {
        public int Id { get; set; }
        public IPAddress Address { get; set; }
        public int Port { get; set; }
        public int StartAddress { get; set; }
        public WordOrder WordOrder { get; set; }
        public float NormalTemperature { get; set; }
        public float NormalVibration { get; set; }
        public float HighTemperature { get; set; }
        public float HighVibration { get; set; }
    }

    internal static class SimulatorSettings
    {
        public static EquipmentSettings[] Load()
        {
            var addressText = Required("ListenAddress");
            if (!IPAddress.TryParse(addressText, out var address))
                throw new ConfigurationErrorsException("ListenAddress must be an IP address.");
            var result = new EquipmentSettings[5];
            var ports = new HashSet<int>();
            for (var i = 0; i < result.Length; i++)
            {
                var prefix = "Equipment." + (i + 1) + ".";
                var port = Integer(prefix + "Port", 1, 65535);
                if (!ports.Add(port))
                    throw new ConfigurationErrorsException(prefix + "Port duplicates another equipment port.");
                var orderText = Required(prefix + "WordOrder");
                if (orderText != "ABCD" && orderText != "CDAB")
                    throw new ConfigurationErrorsException(prefix + "WordOrder must be ABCD or CDAB.");
                result[i] = new EquipmentSettings
                {
                    Id = i + 1, Address = address, Port = port,
                    // FluentModbus 5.3.2 rejects reads that include address 65535.
                    StartAddress = Integer(prefix + "StartAddress", 0, 65530),
                    WordOrder = orderText == "ABCD" ? WordOrder.ABCD : WordOrder.CDAB,
                    NormalTemperature = Number(prefix + "NormalTemperature"),
                    NormalVibration = Number(prefix + "NormalVibration"),
                    HighTemperature = Number(prefix + "HighTemperature"),
                    HighVibration = Number(prefix + "HighVibration")
                };
            }
            return result;
        }

        private static string Required(string key)
        {
            var value = ConfigurationManager.AppSettings[key];
            if (string.IsNullOrWhiteSpace(value))
                throw new ConfigurationErrorsException("Missing setting: " + key);
            return value.Trim();
        }

        private static int Integer(string key, int minimum, int maximum)
        {
            if (!int.TryParse(Required(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                || value < minimum || value > maximum)
                throw new ConfigurationErrorsException(key + " must be in " + minimum + ".." + maximum + ".");
            return value;
        }

        private static float Number(string key)
        {
            if (!TryFiniteNumber(Required(key), out var value))
                throw new ConfigurationErrorsException(key + " must be a finite float32 number.");
            return value;
        }

        internal static bool TryFiniteNumber(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
