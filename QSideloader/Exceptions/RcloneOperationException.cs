using System;

namespace QSideloader.Exceptions;

public class RcloneOperationException : DownloaderServiceException
{
    public RcloneOperationException(string message, Exception inner)
        : base(message, inner)
    {
    }
}