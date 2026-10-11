using System.Collections.Generic;
using System.Linq;

namespace Core
{
    public class TransmissionBatchBuilder
    {
        public static string[] Build(
IReadOnlyList<string> alarms,
IReadOnlyList<string> samples,
int limit

        )
        {   

            return alarms.Concat(samples).Take(limit).ToArray();
        }
    }
}