namespace Core
{
    public class ModbusRetryPolicy
    {
        public static bool ShouldReconnect(ModbusFailureType failureType)
        {
            return failureType == ModbusFailureType.ConnectionFailed || failureType == ModbusFailureType.ConnectionLost;
        }
    }
}