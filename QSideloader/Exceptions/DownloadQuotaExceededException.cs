using System;

namespace QSideloader.Exceptions;

public class DownloadQuotaExceededException : DownloaderServiceException
{
    public DownloadQuotaExceededException(string mirrorName, string remotePath, Exception inner)
        : base($"Quota exceeded on mirror {mirrorName}", inner)
    {
        MirrorName = mirrorName;
        RemotePath = remotePath;
    }

    public string MirrorName { get; }
    public string RemotePath { get; }
}