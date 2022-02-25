using System;

namespace QSideloader.Models;

public class DownloadStats
{
    public DownloadStats(double? speedBytes = null, double? downloadedBytes = null)
    {
        if (speedBytes is null || downloadedBytes is null) return;
        SpeedBytes = speedBytes;
        SpeedMBytes = Math.Round((double) SpeedBytes / 1000000, 2);
        DownloadedBytes = downloadedBytes;
        DownloadedMBytes = Math.Round((double) DownloadedBytes / 1000000, 2);
    }

    public double? SpeedBytes { get; }
    public double? SpeedMBytes { get; }
    public double? DownloadedBytes { get; }
    public double? DownloadedMBytes { get; }
}