using System;
using QSideloader.Services;

namespace QSideloader.Exceptions;

public class NotEnoughDeviceSpaceException(AdbService.AdbDevice device) : Exception($"Not enough space on {device}");
