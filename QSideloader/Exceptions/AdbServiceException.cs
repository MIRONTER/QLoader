using System;

namespace QSideloader.Exceptions;

public class AdbServiceException : Exception
{
    public AdbServiceException(string message)
        : base(message)
    {
    }

    public AdbServiceException(string message, Exception inner)
        : base(message, inner)
    {
    }
}