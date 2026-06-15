namespace MegaDownloaderNext.Core.Links;

public sealed record MegaLink(
    MegaLinkKind Kind,
    string NodeId,
    string Key,
    string OriginalUrl,
    string? SelectedNodeId = null,
    MegaLinkKind? SelectedNodeKind = null)
{
    public string DisplayName => Kind == MegaLinkKind.File
        ? $"File {NodeId}"
        : SelectedNodeKind == MegaLinkKind.Folder && !string.IsNullOrWhiteSpace(SelectedNodeId)
            ? $"Folder {SelectedNodeId}"
            : $"Folder {NodeId}";
}
