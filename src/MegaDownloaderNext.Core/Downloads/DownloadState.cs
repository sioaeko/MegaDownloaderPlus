namespace MegaDownloaderNext.Core.Downloads;

public enum DownloadState
{
    Queued,
    Resolving,
    Downloading,
    Paused,
    BandwidthLimited,
    Skipped,
    Completed,
    Failed,
    Canceled
}
