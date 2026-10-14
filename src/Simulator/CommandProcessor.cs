using System;
using System.Globalization;
using System.Net.Sockets;

namespace Simulator
{
    internal sealed class CommandProcessor
    {
        private readonly Equipment[] equipment;
        public CommandProcessor(Equipment[] equipment) { this.equipment = equipment; }

        public string Execute(string line, out bool quit)
        {
            quit = false;
            try
            {
                var words = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 1 && words[0] == "quit") { quit = true; return "OK"; }
                if (words.Length < 2)
                    throw new ArgumentException("Expected a command and equipment id (1..5).");
                var id = Integer(words[1], 1, 5, "id");
                var machine = equipment[id - 1];
                switch (words[0])
                {
                    case "show":
                        Count(words, 2);
                        return "OK " + machine.Describe();
                    case "delay":
                        Count(words, 3);
                        machine.SetDelay(Integer(words[2], 0, int.MaxValue, "milliseconds"));
                        break;
                    case "disconnect":
                        Count(words, 3);
                        if (Toggle(words[2])) machine.Stop(); else machine.Start();
                        break;
                    case "exception":
                        Count(words, 3);
                        machine.SetException(Toggle(words[2]));
                        break;
                    case "high":
                        Count(words, 3);
                        machine.SetHigh(Toggle(words[2]));
                        break;
                    case "status":
                        Count(words, 3);
                        machine.SetStatus((ushort)Integer(words[2], 0, ushort.MaxValue, "status"));
                        break;
                    case "values":
                        Count(words, 5);
                        if (words[2] != "normal" && words[2] != "high")
                            throw new ArgumentException("Value set must be normal or high.");
                        if (!SimulatorSettings.TryFiniteNumber(words[3], out var temperature)
                            || !SimulatorSettings.TryFiniteNumber(words[4], out var vibration))
                            throw new ArgumentException("Values must be finite float32 numbers (decimal separator: dot).");
                        machine.SetValues(words[2] == "high", temperature, vibration);
                        break;
                    default:
                        throw new ArgumentException("Unknown command: " + words[0]);
                }
                return "OK";
            }
            catch (Exception ex) when (ex is ArgumentException || ex is SocketException)
            {
                return "ERR " + ex.Message.Replace('\r', ' ').Replace('\n', ' ');
            }
        }

        private static void Count(string[] words, int expected)
        {
            if (words.Length != expected) throw new ArgumentException("Wrong number of command arguments.");
        }

        private static bool Toggle(string value)
        {
            if (value == "on") return true;
            if (value == "off") return false;
            throw new ArgumentException("Expected on or off.");
        }

        private static int Integer(string text, int minimum, int maximum, string name)
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                || value < minimum || value > maximum)
                throw new ArgumentException(name + " must be in " + minimum + ".." + maximum + ".");
            return value;
        }
    }
}
