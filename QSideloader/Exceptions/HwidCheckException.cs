using System;

namespace QSideloader.Exceptions;

public class HwidCheckException : DownloaderServiceException
{
    public HwidCheckException(Exception inner)
        : base("Rclone returned HWID check error.", inner)
    {
    }
}