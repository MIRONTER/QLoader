using System;

namespace QSideloader.Exceptions;

public class CleanupException(string? packageName, Exception inner)
    : AdbServiceException($"Cleanup failed for {packageName}", inner);