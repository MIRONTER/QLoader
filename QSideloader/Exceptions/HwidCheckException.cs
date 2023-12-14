using System;

namespace QSideloader.Exceptions;

public class HwidCheckException(Exception inner)
    : DownloaderServiceException("Rclone returned HWID check error.", inner);