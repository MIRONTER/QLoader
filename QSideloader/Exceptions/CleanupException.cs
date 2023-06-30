using System;

namespace QSideloader.Exceptions;

public class CleanupException : AdbServiceException
{
    public CleanupException(string? packageName, Exception inner)
        : base($"Cleanup failed for {packageName}", inner)
    {
    }
}