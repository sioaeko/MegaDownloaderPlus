using MegaDownloaderNext.Core.Links;
using MegaDownloaderNext.Core.Mega;

namespace MegaDownloaderNext.Core.Downloads;

public sealed class DownloadItem
{
    public DownloadItem(MegaLink link, string targetDirectory, MegaFolderFile? folderFile = null)
    {
        Link = link;
        TargetDirectory = targetDirectory;
        FolderFile = folderFile;
        Kind = folderFile is null ? link.Kind : MegaLinkKind.File;
        RelativePath = folderFile?.RelativePath;
        if (folderFile is not null)
        {
            Name = folderFile.Name;
            SizeBytes = folderFile.SizeBytes;
        }
    }

    public Guid Id { get; } = Guid.NewGuid();

    public MegaLink Link { get; }

    public MegaLinkKind Kind { get; }

    public MegaFolderFile? FolderFile { get; }

    public string Name { get; set; } = string.Empty;

    public string? RelativePath { get; }

    public string TargetDirectory { get; set; }

    public string? LocalPath { get; set; }

    public DownloadState State { get; set; } = DownloadState.Queued;

    public long? SizeBytes { get; set; }

    public long DownloadedBytes { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset AddedAt { get; } = DateTimeOffset.UtcNow;

    public double Progress
    {
        get
        {
            if (SizeBytes is null or <= 0)
            {
                return 0;
            }

            return Math.Clamp((double)DownloadedBytes / SizeBytes.Value, 0, 1);
        }
    }

    public string DisplayName => !string.IsNullOrWhiteSpace(RelativePath)
        ? RelativePath
        : string.IsNullOrWhiteSpace(Name) ? Link.DisplayName : Name;
}
