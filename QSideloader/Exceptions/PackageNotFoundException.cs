namespace QSideloader.Exceptions;

public class PackageNotFoundException : AdbServiceException
{
    public PackageNotFoundException(string packageName)
        : base($"Package {packageName} not found")
    {
    }
}