using CG.Web.MegaApiClient;
using MegaDownloaderNext.Core.Links;

namespace MegaDownloaderNext.Core.Mega;

public sealed record MegaFolderExpansion(
    string FolderName,
    IReadOnlyList<MegaFolderFile> Files,
    int SkippedUnreadableNodes);

public sealed record MegaFolderFile(
    MegaLink FolderLink,
    INode Node,
    string RelativePath)
{
    public string Name => Path.GetFileName(RelativePath);

    public long SizeBytes => Node.Size;
}
