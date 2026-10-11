namespace Core
{
    public enum ModbusFailureType
    {
        ConnectionFailed,
        ConnectionLost,
        Timeout,
        DeviceException,
        InvalidValue
    }
}