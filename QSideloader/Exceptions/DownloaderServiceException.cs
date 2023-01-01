using System;

namespace QSideloader.Exceptions;

public class DownloaderServiceException : Exception
{
    public DownloaderServiceException(string message)
        : base(message)
    {
    }

    public DownloaderServiceException(string message, Exception inner)
        : base(message, inner)
    {
    }
}