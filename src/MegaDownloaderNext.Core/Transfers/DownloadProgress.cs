namespace MegaDownloaderNext.Core.Transfers;

public sealed record DownloadProgress(long DownloadedBytes, long? TotalBytes);

