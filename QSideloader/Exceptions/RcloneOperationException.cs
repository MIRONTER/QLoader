using System;

namespace QSideloader.Exceptions;

public class RcloneOperationException(string message, Exception inner) : DownloaderServiceException(message, inner);