using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace MegaDownloaderNext.Core.Downloads;

public static class ArchiveExtractor
{
    private static readonly string[] SupportedExtensions = [".zip", ".rar", ".7z", ".tar", ".gz"];

    public static bool IsSupported(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return SupportedExtensions.Contains(extension);
    }

    public static async Task ExtractAsync(
        string archivePath,
        string destinationDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions());
            var entries = archive.Entries.Where(entry => !entry.IsDirectory).ToList();
            var totalFiles = entries.Count;
            var extractedFiles = 0;
            var destinationRoot = Path.GetFullPath(destinationDirectory);

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Extracting: {entry.Key} ({extractedFiles + 1}/{totalFiles})");
                var destinationPath = GetSafeDestinationPath(destinationRoot, entry.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? destinationRoot);
                entry.WriteToFile(destinationPath, new ExtractionOptions
                {
                    Overwrite = true
                });
                extractedFiles++;
            }
        }, cancellationToken);
    }

    private static string GetSafeDestinationPath(string destinationRoot, string? entryKey)
    {
        if (string.IsNullOrWhiteSpace(entryKey))
        {
            throw new InvalidDataException("Archive entry has no path.");
        }

        var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, entryKey));
        var rootWithSeparator = destinationRoot.EndsWith(Path.DirectorySeparatorChar)
            ? destinationRoot
            : destinationRoot + Path.DirectorySeparatorChar;

        if (!destinationPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(destinationPath, destinationRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Archive entry path escapes the extraction directory: {entryKey}");
        }

        return destinationPath;
    }
}
