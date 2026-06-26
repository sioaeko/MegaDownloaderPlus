using MegaDownloaderNext.Core.Links;
using MegaDownloaderNext.Core.Mega;

namespace MegaDownloaderNext.Core.Downloads;

public sealed class DownloadQueue
{
    private readonly List<DownloadItem> _items = [];

    public event EventHandler? Changed;

    public IReadOnlyList<DownloadItem> Items => _items;

    public DownloadItem Add(MegaLink link, string targetDirectory)
    {
        var item = new DownloadItem(link, targetDirectory);
        _items.Add(item);
        Changed?.Invoke(this, EventArgs.Empty);
        return item;
    }

    public bool ContainsEquivalent(MegaLink link, string targetDirectory)
    {
        return _items.Any(item =>
            item.FolderFile is null
            && IsSameLink(item.Link, link)
            && IsSameTargetDirectory(item.TargetDirectory, targetDirectory));
    }

    public bool ContainsEquivalent(MegaFolderFile file, string targetDirectory)
    {
        return _items.Any(item =>
            item.FolderFile is { } queuedFile
            && IsSameLink(item.Link, file.FolderLink)
            && string.Equals(queuedFile.Node.Id, file.Node.Id, StringComparison.Ordinal)
            && string.Equals(queuedFile.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase)
            && IsSameTargetDirectory(item.TargetDirectory, targetDirectory));
    }

    public DownloadItem Add(MegaFolderFile file, string targetDirectory)
    {
        var item = new DownloadItem(file.FolderLink, targetDirectory, file);
        _items.Add(item);
        Changed?.Invoke(this, EventArgs.Empty);
        return item;
    }

    public int AddMany(IEnumerable<MegaLink> links, string targetDirectory)
    {
        var count = 0;

        foreach (var link in links)
        {
            Add(link, targetDirectory);
            count++;
        }

        return count;
    }

    public void Remove(DownloadItem item)
    {
        if (_items.Remove(item))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Clear()
    {
        if (_items.Count == 0)
        {
            return;
        }

        _items.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearCompleted()
    {
        _items.RemoveAll(item => item.State is DownloadState.Completed or DownloadState.Canceled or DownloadState.Skipped);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsSameLink(MegaLink left, MegaLink right)
    {
        return left.Kind == right.Kind
            && string.Equals(left.NodeId, right.NodeId, StringComparison.Ordinal)
            && string.Equals(left.Key, right.Key, StringComparison.Ordinal)
            && string.Equals(left.SelectedNodeId, right.SelectedNodeId, StringComparison.Ordinal)
            && left.SelectedNodeKind == right.SelectedNodeKind;
    }

    private static bool IsSameTargetDirectory(string left, string right)
    {
        return string.Equals(
            NormalizeTargetDirectory(left),
            NormalizeTargetDirectory(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTargetDirectory(string path)
    {
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
