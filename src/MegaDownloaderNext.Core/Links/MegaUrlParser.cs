using System.Text.RegularExpressions;

namespace MegaDownloaderNext.Core.Links;

public static partial class MegaUrlParser
{
    public static bool TryParse(string? input, out MegaLink link)
    {
        link = default!;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var candidate = NormalizeCandidate(CleanCandidate(input));

        foreach (var parser in Parsers)
        {
            var match = parser.Pattern.Match(candidate);
            if (!match.Success)
            {
                continue;
            }

            var nodeId = match.Groups["id"].Value;
            var key = match.Groups["key"].Value;
            if (!IsToken(nodeId) || !IsToken(key))
            {
                return false;
            }

            var (selectedNodeId, selectedNodeKind) = ParseSelectionPath(
                match.Groups["selectionPath"].Success ? match.Groups["selectionPath"].Value : string.Empty);
            if (selectedNodeId is not null && !IsToken(selectedNodeId))
            {
                return false;
            }

            link = new MegaLink(parser.Kind, nodeId, key, candidate, selectedNodeId, selectedNodeKind);
            return true;
        }

        return false;
    }

    public static IReadOnlyList<MegaLink> ParseMany(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<MegaLink>();
        }

        var links = new List<MegaLink>();
        foreach (var token in Tokenize(text))
        {
            if (TryParse(token, out var link))
            {
                links.Add(link);
            }
        }

        return links;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in MegaUrlCandidateRegex().Matches(text))
        {
            var candidate = CleanCandidate(match.Value);
            if (candidate.Length > 0 && seen.Add(candidate))
            {
                yield return candidate;
            }
        }

        foreach (var token in text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = CleanCandidate(token);
            if (candidate.Length > 0 && seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static string CleanCandidate(string token)
    {
        var cleaned = token
            .Trim()
            .Replace("\u200B", string.Empty)
            .Replace("\u200C", string.Empty)
            .Replace("\u200D", string.Empty)
            .Replace("\uFEFF", string.Empty)
            .Trim('<', '>', '"', '\'', '`', ',', ';', '.', ':', ')', ']', '}', '）', '】', '〉', '》', '」', '』', '，', '；', '。');

        return NormalizeCandidate(cleaned);
    }

    private static string NormalizeCandidate(string candidate)
    {
        if (candidate.StartsWith("www.mega.", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("mega.nz/", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("mega.co.nz/", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("mega.app/", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://{candidate}";
        }

        return candidate;
    }

    private static bool IsToken(string value)
    {
        return value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
    }

    private static MegaLinkKind ParseSelectedKind(string value)
    {
        return value.Equals("file", StringComparison.OrdinalIgnoreCase)
            ? MegaLinkKind.File
            : MegaLinkKind.Folder;
    }

    private static (string? NodeId, MegaLinkKind? Kind) ParseSelectionPath(string selectionPath)
    {
        if (string.IsNullOrWhiteSpace(selectionPath))
        {
            return (null, null);
        }

        var matches = SelectionSegmentRegex().Matches(selectionPath);
        if (matches.Count == 0)
        {
            return (null, null);
        }

        var lastMatch = matches[matches.Count - 1];
        return (lastMatch.Groups["id"].Value, ParseSelectedKind(lastMatch.Groups["kind"].Value));
    }

    private static readonly LinkParser[] Parsers =
    [
        new(MegaLinkKind.File, NewFileLinkRegex()),
        new(MegaLinkKind.Folder, NewFolderLinkRegex()),
        new(MegaLinkKind.File, OldFileLinkRegex()),
        new(MegaLinkKind.Folder, OldFolderLinkRegex()),
        new(MegaLinkKind.File, MegaSchemeFileLinkRegex()),
        new(MegaLinkKind.Folder, MegaSchemeFolderLinkRegex())
    ];

    private sealed record LinkParser(MegaLinkKind Kind, Regex Pattern);

    [GeneratedRegex(@"(?:(?:https?://)?(?:www\.)?(?:mega(?:\.co)?\.nz|mega\.app)|mega://)\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MegaUrlCandidateRegex();

    [GeneratedRegex(@"^https?://(?:www\.)?(?:mega(?:\.co)?\.nz|mega\.app)/file/(?<id>[A-Za-z0-9_-]+)#(?<key>[A-Za-z0-9_-]+)(?:[?&].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NewFileLinkRegex();

    [GeneratedRegex(@"^https?://(?:www\.)?(?:mega(?:\.co)?\.nz|mega\.app)/folder/(?<id>[A-Za-z0-9_-]+)#(?<key>[A-Za-z0-9_-]+)(?<selectionPath>(?:/(?:folder|file)/[A-Za-z0-9_-]+)*)(?:[?&].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NewFolderLinkRegex();

    [GeneratedRegex(@"/(?<kind>folder|file)/(?<id>[A-Za-z0-9_-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SelectionSegmentRegex();

    [GeneratedRegex(@"^https?://(?:www\.)?(?:mega(?:\.co)?\.nz|mega\.app)/#!(?<id>[A-Za-z0-9_-]+)!(?<key>[A-Za-z0-9_-]+)(?:[?&].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OldFileLinkRegex();

    [GeneratedRegex(@"^https?://(?:www\.)?(?:mega(?:\.co)?\.nz|mega\.app)/#F!(?<id>[A-Za-z0-9_-]+)!(?<key>[A-Za-z0-9_-]+)(?:[?&].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OldFolderLinkRegex();

    [GeneratedRegex(@"^mega://#?!(?<id>[A-Za-z0-9_-]+)!(?<key>[A-Za-z0-9_-]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MegaSchemeFileLinkRegex();

    [GeneratedRegex(@"^mega://#?F!(?<id>[A-Za-z0-9_-]+)!(?<key>[A-Za-z0-9_-]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MegaSchemeFolderLinkRegex();
}
