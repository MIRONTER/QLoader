using System;

namespace QSideloader.Exceptions;

public class DownloadQuotaExceededException(string mirrorName, Exception inner)
    : DownloaderServiceException($"Quota exceeded on mirror {mirrorName}", inner);