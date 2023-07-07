namespace QSideloader.Models;

public struct ApkInfo
{
    public string ApplicationLabel { get; init; }
    public string PackageName { get; init; }
    public string VersionName { get; init; }
    public int VersionCode { get; init; }
}