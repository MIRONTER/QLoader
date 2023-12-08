using System;
using QSideloader.Services;

namespace QSideloader.Exceptions;

public class NotEnoughDeviceSpaceException: Exception
{
    public NotEnoughDeviceSpaceException(AdbService.AdbDevice device) : base($"Not enough disk space on {device}")
    {
        Device = device;
    }
    
    public AdbService.AdbDevice Device { get; }
}