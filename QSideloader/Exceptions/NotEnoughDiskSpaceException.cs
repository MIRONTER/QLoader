using System;

namespace QSideloader.Exceptions;

public class NotEnoughDiskSpaceException(string path, Exception inner)
    : DownloaderServiceException($"Not enough disk space on {path}", inner);