using System;

namespace QSideloader.Exceptions;

public class NotEnoughDiskSpaceException : DownloaderServiceException
{
    public NotEnoughDiskSpaceException(string path, Exception inner)
        : base($"Not enough disk space on {path}", inner)
    {
        Path = path;
    }

    public string Path { get; }
}