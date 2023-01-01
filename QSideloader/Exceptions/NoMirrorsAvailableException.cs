namespace QSideloader.Exceptions;

public class NoMirrorsAvailableException : DownloaderServiceException
{
    public NoMirrorsAvailableException(bool session, int excludedCount)
        : base(session
            ? $"No mirrors available for this session ({excludedCount} excluded)"
            : $"No mirrors available for this download ({excludedCount} excluded)")
    {
    }
}