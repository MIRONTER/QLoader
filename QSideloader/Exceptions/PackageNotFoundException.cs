namespace QSideloader.Exceptions;

public class PackageNotFoundException(string packageName) : AdbServiceException($"Package {packageName} not found");