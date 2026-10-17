using Core;
using System;

namespace Modbus
{
    public class ModbusReadResult
    {
        public byte[] Data {get;}
        public ModbusFailureType? FailureType {get;}
        public byte? ExceptionCode {get;}

        public ModbusReadResult(byte[] data , ModbusFailureType? failureType, byte? exceptionCode)
        {   
            if(data != null && failureType.HasValue)
            {
                throw new ArgumentException("성공 데이터와 실패 유형을 동시에 지정할 수 없음");
            }
            else if(data == null && !failureType.HasValue)
            {
                throw new ArgumentException("성공 데이터 또는 실패 유형 중 하나는 반드시 지정해야 함");
            }
            if(failureType == ModbusFailureType.DeviceException && !exceptionCode.HasValue)
            {
                throw new ArgumentException("failureType이 DeviceException인데 exceptionCode가 없으면 거부");
            }
            if(failureType != ModbusFailureType.DeviceException && exceptionCode.HasValue)
            {
                throw new ArgumentException("failureType이 DeviceException이 아닌데 exceptionCode가 있으면 거부");
            }

            this.Data=data;
            this.FailureType=failureType;
            this.ExceptionCode=exceptionCode;
        }
    }
}