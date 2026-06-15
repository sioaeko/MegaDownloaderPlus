using CG.Web.MegaApiClient;
using MegaDownloaderNext.Core.Links;
using MegaDownloaderNext.Core.Transfers;
using Polly;
using Polly.Retry;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MegaDownloaderNext.Core.Mega;

public sealed class MegaApiTransferService : IDisposable
{
    private readonly MegaApiClient _client = new();
    private readonly HttpClient _apiHttpClient = new();
    private readonly HttpClient _downloadHttpClient = new();
    private readonly HttpMegaDownloadEngine _downloadEngine;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private MegaAccountSession? _accountSession;
    private LoginMode _loginMode = LoginMode.None;
    private bool _disposed;
    private long _apiRequestId;

    private readonly ResiliencePipeline _resiliencePipeline;

    public MegaApiTransferService()
    {
        _downloadEngine = new HttpMegaDownloadEngine(_downloadHttpClient);
        _resiliencePipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex =>
                    ex is HttpRequestException ||
                    ex is System.Net.Sockets.SocketException ||
                    ex is System.Security.Authentication.AuthenticationException ||
                    (ex is ApiException apiEx && (apiEx.ApiResultCode == ApiResultCode.RequestFailedRetry || apiEx.ApiResultCode == ApiResultCode.BadSessionId))),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxRetryAttempts = 5,
                Delay = TimeSpan.FromSeconds(2)
            })
            .Build();
    }

    public async Task<IReadOnlyList<MegaFolderFile>> ExpandFolderAsync(MegaLink folderLink, CancellationToken cancellationToken)
    {
        return await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            var expansion = await ExpandFolderWithReportAsync(folderLink, ct).ConfigureAwait(false);
            return expansion.Files;
        }, cancellationToken);
    }

    public async Task<MegaFolderExpansion> ExpandFolderWithReportAsync(MegaLink folderLink, CancellationToken cancellationToken)
    {
        if (folderLink.Kind != MegaLinkKind.Folder)
        {
            throw new MegaApiException("Only folder links can be expanded.");
        }

        return await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            var rawFolderTree = await GetRawFolderTreeAsync(folderLink, ct).ConfigureAwait(false);
            var scope = ResolveRawFolderScope(folderLink, rawFolderTree);
            var scopedFileNodes = rawFolderTree.NodesById.Values
                .Where(node => node.Type == 0)
                .Where(node => IsInRawFolderScope(node, scope, rawFolderTree))
                .ToList();

            var unreadableFiles = 0;
            var files = scopedFileNodes
                .Select(node =>
                {
                    var relativePath = TryBuildRawRelativePath(
                        node,
                        rawFolderTree,
                        scope.StopAtFolderId ?? folderLink.NodeId);
                    if (relativePath is not null && node.FullKey.Length > 0)
                    {
                        return new MegaFolderFile(
                            folderLink,
                            new MegaFileNodeSnapshot(
                                node.Id,
                                Path.GetFileName(relativePath),
                                node.SizeBytes,
                                node.ParentId,
                                node.FullKey),
                            relativePath);
                    }

                    unreadableFiles++;
                    return null;
                })
                .OfType<MegaFolderFile>()
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new MegaFolderExpansion(scope.FolderName, files, unreadableFiles);
        }, cancellationToken);
    }

    public async Task<INode> GetFileNodeAsync(MegaLink fileLink, CancellationToken cancellationToken)
    {
        if (fileLink.Kind != MegaLinkKind.File)
        {
            throw new MegaApiException("Only file links can be resolved.");
        }

        return await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            await EnsureLoggedInAsync(ct).ConfigureAwait(false);
            return await _client.GetNodeFromLinkAsync(new Uri(fileLink.OriginalUrl)).ConfigureAwait(false);     
        }, cancellationToken);
    }

    public void ConfigureAccountSession(MegaAccountSession? session)
    {
        _loginLock.Wait();
        try
        {
            _accountSession = NormalizeAccountSession(session);
            _loginMode = LoginMode.None;
            LogoutCurrentClient();
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public void ClearAccountSession()
    {
        ConfigureAccountSession(null);
    }

    public async Task<MegaAccountSession> LoginWithAccountAsync(
        string email,
        string password,
        string? mfaKey,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("MEGA password is required.", nameof(password));
        }

        await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogoutCurrentClient();

            var normalizedMfaKey = string.IsNullOrWhiteSpace(mfaKey) ? null : mfaKey.Trim();
            var authInfos = normalizedMfaKey is null
                ? await _client.GenerateAuthInfosAsync(normalizedEmail, password).ConfigureAwait(false)
                : await _client.GenerateAuthInfosAsync(normalizedEmail, password, normalizedMfaKey).ConfigureAwait(false);
            var token = await _client.LoginAsync(authInfos).ConfigureAwait(false);
            var session = new MegaAccountSession(
                normalizedEmail,
                token.SessionId,
                Convert.ToBase64String(token.MasterKey));

            _accountSession = NormalizeAccountSession(session)!;
            _loginMode = LoginMode.Account;
            return _accountSession;
        }
        catch
        {
            _loginMode = LoginMode.None;
            throw;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public async Task DownloadFileLinkAsync(
        MegaLink fileLink,
        INode node,
        string outputPath,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        if (fileLink.Kind != MegaLinkKind.File)
        {
            throw new MegaApiException("Only file links can be downloaded with this method.");
        }

        await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            var downloadUrl = await GetPublicFileDownloadUrlAsync(fileLink, ct).ConfigureAwait(false);
            var key = BuildEngineKey(fileLink, node);

            await _downloadEngine.DownloadAsync(
                downloadUrl,
                key,
                outputPath,
                node.Size,
                progress,
                ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public async Task DownloadFolderFileAsync(
        MegaFolderFile folderFile,
        string outputPath,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            var downloadUrl = await GetPublicFolderFileDownloadUrlAsync(folderFile, ct).ConfigureAwait(false);
            var key = BuildEngineKey(folderFile.FolderLink, folderFile.Node);

            await _downloadEngine.DownloadAsync(
                downloadUrl,
                key,
                outputPath,
                folderFile.SizeBytes,
                progress,
                ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loginLock.Wait();
        try
        {
            LogoutCurrentClient();
        }
        finally
        {
            _loginLock.Release();
            _loginLock.Dispose();
        }

        _apiHttpClient.Dispose();
        _downloadHttpClient.Dispose();
    }

    private async Task EnsureLoggedInAsync(CancellationToken cancellationToken = default)
    {
        if (IsDesiredLoginModeActive())
        {
            return;
        }

        await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsDesiredLoginModeActive())
            {
                return;
            }

            LogoutCurrentClient();

            if (_accountSession is { } accountSession)
            {
                await _client.LoginAsync(ToLogonSessionToken(accountSession)).ConfigureAwait(false);
                _loginMode = LoginMode.Account;
                return;
            }

            await _client.LoginAnonymousAsync().ConfigureAwait(false);
            _loginMode = LoginMode.Anonymous;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private Task EnsureAccountLoggedInIfConfiguredAsync(CancellationToken cancellationToken)
    {
        return _accountSession is null
            ? Task.CompletedTask
            : EnsureLoggedInAsync(cancellationToken);
    }

    private bool IsDesiredLoginModeActive()
    {
        if (!_client.IsLoggedIn)
        {
            return false;
        }

        return _accountSession is null
            ? _loginMode == LoginMode.Anonymous
            : _loginMode == LoginMode.Account;
    }

    private void LogoutCurrentClient()
    {
        if (!_client.IsLoggedIn)
        {
            return;
        }

        try
        {
            _client.Logout();
        }
        catch (NotSupportedException)
        {
        }
    }

    private static MegaAccountSession? NormalizeAccountSession(MegaAccountSession? session)
    {
        if (session is null)
        {
            return null;
        }

        var email = NormalizeEmail(session.Email);
        if (string.IsNullOrWhiteSpace(session.SessionId))
        {
            throw new ArgumentException("MEGA session id is required.", nameof(session));
        }

        var masterKey = Convert.FromBase64String(session.MasterKeyBase64);
        if (masterKey.Length == 0)
        {
            throw new ArgumentException("MEGA master key is required.", nameof(session));
        }

        return new MegaAccountSession(email, session.SessionId.Trim(), Convert.ToBase64String(masterKey));
    }

    private static string NormalizeEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("MEGA email is required.", nameof(email));
        }

        return email.Trim();
    }

    private static MegaApiClient.LogonSessionToken ToLogonSessionToken(MegaAccountSession session)
    {
        return new MegaApiClient.LogonSessionToken(
            session.SessionId,
            Convert.FromBase64String(session.MasterKeyBase64));
    }

    private static Uri BuildRootFolderUri(MegaLink folderLink)
    {
        return new Uri($"https://mega.nz/folder/{folderLink.NodeId}#{folderLink.Key}");
    }

    private static Uri BuildSelectedFolderUri(MegaLink folderLink, string folderNodeId)
    {
        return new Uri($"https://mega.nz/folder/{folderLink.NodeId}#{folderLink.Key}/folder/{folderNodeId}");
    }

    private async Task<RawFolderTree> GetRawFolderTreeAsync(
        MegaLink folderLink,
        CancellationToken cancellationToken)
    {
        await EnsureAccountLoggedInIfConfiguredAsync(cancellationToken).ConfigureAwait(false);

        var requestId = Interlocked.Increment(ref _apiRequestId);
        var endpoint = BuildApiEndpoint(requestId, folderLink.NodeId);
        var payload = JsonSerializer.Serialize(new[] { new MegaFolderNodesRequest() });

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var response = await _apiHttpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
        {
            throw new MegaApiException("MEGA returned an empty folder response.");
        }

        var first = document.RootElement[0];
        if (first.ValueKind == JsonValueKind.Number && first.TryGetInt32(out var apiError))
        {
            throw new MegaApiException(FormatApiError(apiError));
        }

        if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("f", out var nodesElement))
        {
            throw new MegaApiException("MEGA did not return a folder node list.");
        }

        var rawNodes = new List<RawMegaNode>();
        foreach (var nodeElement in nodesElement.EnumerateArray())
        {
            var rawNode = RawMegaNode.From(nodeElement);
            if (rawNode is null)
            {
                continue;
            }

            rawNodes.Add(rawNode);
        }

        if (rawNodes.Count == 0)
        {
            throw new MegaApiException("MEGA returned an empty folder node list.");
        }

        return DecryptRawFolderTree(rawNodes, DecodeBase64Url(folderLink.Key), folderLink.NodeId);
    }

    private static RawFolderTree DecryptRawFolderTree(
        IReadOnlyList<RawMegaNode> nodes,
        byte[] sharedKey,
        string rootFolderId)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var nodesById = new Dictionary<string, RawFolderNodeInfo>(StringComparer.Ordinal);
        var knownFolderKeys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var sharedFolderKey = sharedKey.Take(16).ToArray();
        knownFolderKeys[rootFolderId] = sharedFolderKey;

        var madeProgress = true;
        while (madeProgress)
        {
            madeProgress = false;

            foreach (var node in nodes)
            {
                if (nodesById.TryGetValue(node.Id, out var existingNode)
                    && existingNode.HasDecryptedName)
                {
                    continue;
                }

                RawFolderNodeInfo? fallbackFolder = null;
                foreach (var unlockKey in EnumerateUnlockKeys(node, knownFolderKeys, sharedFolderKey))
                {
                    foreach (var nodeKey in EnumerateDecryptedRawNodeKeys(node, unlockKey))
                    {
                        var name = TryDecryptNameFromSerializedAttributes(
                            node.SerializedAttributes,
                            GetAttributeKey(node.Type, nodeKey));
                        if (!IsUsableName(name))
                        {
                            if (node.Type != 0 && fallbackFolder is null)
                            {
                                fallbackFolder = new RawFolderNodeInfo(
                                    node.Id,
                                    node.ParentId,
                                    node.Type,
                                    GetFallbackFolderName(node),
                                    node.SizeBytes,
                                    nodeKey,
                                    false);
                            }

                            continue;
                        }

                        names[node.Id] = name!;
                        nodesById[node.Id] = new RawFolderNodeInfo(
                            node.Id,
                            node.ParentId,
                            node.Type,
                            name!,
                            node.SizeBytes,
                            nodeKey,
                            true);
                        if (node.Type != 0)
                        {
                            knownFolderKeys[node.Id] = nodeKey.Take(16).ToArray();
                        }

                        madeProgress = true;
                        goto NextNode;
                    }
                }

                if (fallbackFolder is not null && !nodesById.ContainsKey(node.Id))
                {
                    nodesById[node.Id] = fallbackFolder;
                    knownFolderKeys[node.Id] = fallbackFolder.FullKey.Take(16).ToArray();
                    madeProgress = true;
                }

            NextNode:
                continue;
            }
        }

        if (nodes.Count > 0 && nodesById.Count == 0)
        {
            throw new MegaApiException("MEGA folder metadata could not be decrypted with this link key.");
        }

        return new RawFolderTree(nodesById, names);
    }

    private static IEnumerable<byte[]> EnumerateUnlockKeys(
        RawMegaNode node,
        IReadOnlyDictionary<string, byte[]> knownFolderKeys,
        byte[] sharedFolderKey)
    {
        if (!string.IsNullOrWhiteSpace(node.ParentId)
            && knownFolderKeys.TryGetValue(node.ParentId, out var parentKey))
        {
            yield return parentKey;
        }

        foreach (var key in knownFolderKeys.Values)
        {
            if (!key.SequenceEqual(sharedFolderKey))
            {
                yield return key;
            }
        }

        yield return sharedFolderKey;
    }

    private static IReadOnlyList<byte[]> EnumerateDecryptedRawNodeKeys(RawMegaNode node, byte[] unlockKey)
    {
        var keys = new List<byte[]>();
        if (string.IsNullOrWhiteSpace(node.SerializedKey))
        {
            if (node.Type != 0)
            {
                keys.Add(unlockKey.Take(16).ToArray());
            }

            return keys;
        }

        foreach (var candidate in EnumerateSerializedKeyCandidates(node.SerializedKey))
        {
            try
            {
                var encryptedKey = DecodeBase64Url(candidate);
                if (encryptedKey.Length == 0 || encryptedKey.Length % 16 != 0)
                {
                    continue;
                }

                keys.Add(DecryptAesEcb(encryptedKey, unlockKey.Take(16).ToArray()));
            }
            catch (FormatException)
            {
            }
            catch (CryptographicException)
            {
            }
        }

        return keys;
    }

    private static IEnumerable<string> EnumerateSerializedKeyCandidates(string serializedKey)
    {
        foreach (var segment in serializedKey.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = segment.IndexOf(':');
            var candidate = separator >= 0 ? segment[(separator + 1)..] : segment;
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static byte[] GetAttributeKey(int nodeType, byte[] nodeKey)
    {
        if (nodeType == 0 && nodeKey.Length >= 32)
        {
            var key = new byte[16];
            for (var i = 0; i < key.Length; i++)
            {
                key[i] = (byte)(nodeKey[i] ^ nodeKey[i + 16]);
            }

            return key;
        }

        return nodeKey.Take(16).ToArray();
    }

    private static byte[] DecryptAesEcb(byte[] encrypted, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
    }

    private async Task<List<INode>> GetFolderNodesDeepAsync(MegaLink folderLink, CancellationToken cancellationToken)
    {
        var nodes = new List<INode>();
        var seenNodes = new HashSet<string>(StringComparer.Ordinal);
        var expandedFolders = new HashSet<string>(StringComparer.Ordinal);
        var pendingUris = new Queue<Uri>();

        pendingUris.Enqueue(BuildRootFolderUri(folderLink));

        while (pendingUris.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = (await _client.GetNodesFromLinkAsync(pendingUris.Dequeue()).ConfigureAwait(false)).ToList();
            foreach (var node in batch)
            {
                if (RememberNode(seenNodes, node))
                {
                    nodes.Add(node);
                }
            }

            foreach (var folder in batch.Where(IsFolderNode))
            {
                var folderHandle = folder.Id;
                if (!batch.Any(candidate => candidate.ParentId == folder.Id)
                    && expandedFolders.Add(folderHandle))
                {
                    pendingUris.Enqueue(BuildSelectedFolderUri(folderLink, folderHandle));
                }
            }
        }

        return nodes;
    }

    private static bool RememberNode(HashSet<string> seenNodes, INode node)
    {
        var nodeId = node.Id;
        var shareId = GetNodeShareId(node);
        var primaryId = !string.IsNullOrWhiteSpace(nodeId) ? nodeId : shareId;
        if (string.IsNullOrWhiteSpace(primaryId) || !seenNodes.Add(primaryId))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(shareId))
        {
            seenNodes.Add(shareId);
        }

        return true;
    }

    private static bool IsFolderNode(INode node)
    {
        return node.Type != NodeType.File && node.Type != NodeType.Root;
    }

    private static Dictionary<string, INode> BuildNodeLookup(IEnumerable<INode> nodes)
    {
        var lookup = new Dictionary<string, INode>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            AddNodeLookupValue(lookup, node.Id, node);
            AddNodeLookupValue(lookup, GetNodeShareId(node), node);
        }

        return lookup;
    }

    private static void AddNodeLookupValue(Dictionary<string, INode> lookup, string? id, INode node)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            lookup.TryAdd(id, node);
        }
    }

    private static FolderScope ResolveFolderScope(
        MegaLink folderLink,
        IReadOnlyList<INode> nodes,
        IReadOnlyDictionary<string, INode> nodesById,
        RawFolderTree rawFolderTree)
    {
        var decryptedNames = rawFolderTree.NamesById;
        if (!string.IsNullOrWhiteSpace(folderLink.SelectedNodeId))
        {
            var selectedRawNode = rawFolderTree.NodesById.TryGetValue(folderLink.SelectedNodeId, out var rawNode)
                ? rawNode
                : null;
            nodesById.TryGetValue(folderLink.SelectedNodeId, out var selectedNode);

            if (selectedRawNode is null && selectedNode is null)
            {
                throw new MegaApiException("Selected MEGA subfolder was not found in this public folder.");
            }

            if (folderLink.SelectedNodeKind == MegaLinkKind.File)
            {
                if (selectedRawNode is { Type: not 0 }
                    || selectedNode is not null && selectedNode.Type != NodeType.File)
                {
                    throw new MegaApiException("Selected MEGA file was not found in this public folder.");
                }

                var parentId = selectedRawNode?.ParentId ?? selectedNode?.ParentId;
                var parentName = !string.IsNullOrWhiteSpace(parentId)
                    && rawFolderTree.NodesById.TryGetValue(parentId, out var rawParent)
                        ? rawParent.Name
                        : !string.IsNullOrWhiteSpace(parentId)
                            && nodesById.TryGetValue(parentId, out var parent)
                            && GetBestNodeName(parent, decryptedNames) is { } bestParentName
                                ? bestParentName
                                : GetRootFolderName(folderLink, nodes, rawFolderTree);

                return new FolderScope(parentName, parentId, folderLink.SelectedNodeId);
            }

            if (selectedRawNode is { Type: 0 }
                || selectedNode is not null && selectedNode.Type == NodeType.File)
            {
                throw new MegaApiException("Selected MEGA node is a file, not a folder.");
            }

            var folderName = selectedRawNode?.Name
                ?? (selectedNode is not null
                    ? GetSafeNodeName(selectedNode, decryptedNames)
                    : throw new MegaApiException("Selected MEGA subfolder name could not be decrypted."));
            return new FolderScope(folderName, folderLink.SelectedNodeId, null);
        }

        return new FolderScope(GetRootFolderName(folderLink, nodes, rawFolderTree), null, null);
    }

    private static FolderScope ResolveRawFolderScope(MegaLink folderLink, RawFolderTree rawFolderTree)
    {
        if (!string.IsNullOrWhiteSpace(folderLink.SelectedNodeId))
        {
            if (!rawFolderTree.NodesById.TryGetValue(folderLink.SelectedNodeId, out var selectedNode))
            {
                throw new MegaApiException("Selected MEGA node was not found in this public folder.");
            }

            if (folderLink.SelectedNodeKind == MegaLinkKind.File)
            {
                if (selectedNode.Type != 0)
                {
                    throw new MegaApiException("Selected MEGA file was not found in this public folder.");
                }

                var parentName = !string.IsNullOrWhiteSpace(selectedNode.ParentId)
                    && rawFolderTree.NodesById.TryGetValue(selectedNode.ParentId, out var parent)
                        ? parent.Name
                        : GetRawRootFolderName(folderLink, rawFolderTree);

                return new FolderScope(parentName, selectedNode.ParentId, selectedNode.Id);
            }

            if (selectedNode.Type == 0)
            {
                throw new MegaApiException("Selected MEGA node is a file, not a folder.");
            }

            return new FolderScope(selectedNode.Name, selectedNode.Id, null);
        }

        var rootNode = GetRawRootNode(folderLink, rawFolderTree);
        return new FolderScope(rootNode?.Name ?? "MEGA 폴더", rootNode?.Id, null);
    }

    private static string GetRawRootFolderName(MegaLink folderLink, RawFolderTree rawFolderTree)
    {
        return GetRawRootNode(folderLink, rawFolderTree)?.Name ?? "MEGA 폴더";
    }

    private static RawFolderNodeInfo? GetRawRootNode(MegaLink folderLink, RawFolderTree rawFolderTree)
    {
        if (rawFolderTree.NodesById.TryGetValue(folderLink.NodeId, out var rawRoot)
            && IsUsableName(rawRoot.Name))
        {
            return rawRoot;
        }

        var topLevelFolders = rawFolderTree.NodesById.Values
            .Where(node => node.Type != 0)
            .Where(node => string.IsNullOrWhiteSpace(node.ParentId)
                || !rawFolderTree.NodesById.ContainsKey(node.ParentId))
            .ToList();

        return topLevelFolders.Count == 1 && IsUsableName(topLevelFolders[0].Name)
            ? topLevelFolders[0]
            : null;
    }

    private static string GetRootFolderName(
        MegaLink folderLink,
        IReadOnlyList<INode> nodes,
        RawFolderTree rawFolderTree)
    {
        if (rawFolderTree.NodesById.TryGetValue(folderLink.NodeId, out var rawRoot)
            && IsUsableName(rawRoot.Name))
        {
            return rawRoot.Name;
        }

        var root = nodes.FirstOrDefault(node => node.Type == NodeType.Root);
        var decryptedNames = rawFolderTree.NamesById;
        return root is not null && GetBestNodeName(root, decryptedNames) is { } rootName
            ? rootName
            : "MEGA 폴더";
    }

    private static bool IsInFolderScope(
        INode node,
        FolderScope scope,
        IReadOnlyDictionary<string, INode> nodesById,
        RawFolderTree rawFolderTree)
    {
        if (!string.IsNullOrWhiteSpace(scope.SelectedFileId))
        {
            return node.Id == scope.SelectedFileId || GetNodeShareId(node) == scope.SelectedFileId;
        }

        if (string.IsNullOrWhiteSpace(scope.StopAtFolderId))
        {
            return true;
        }

        var rawNode = TryGetRawNodeInfo(node, rawFolderTree);
        if (rawNode is not null)
        {
            return HasRawAncestor(rawNode, scope.StopAtFolderId, rawFolderTree);
        }

        var parentId = node.ParentId;
        while (!string.IsNullOrWhiteSpace(parentId))
        {
            if (parentId == scope.StopAtFolderId)
            {
                return true;
            }

            if (!nodesById.TryGetValue(parentId, out var parent) || parent.Type == NodeType.Root)
            {
                break;
            }

            parentId = parent.ParentId;
        }

        return false;
    }

    private static bool IsInRawFolderScope(
        RawFolderNodeInfo node,
        FolderScope scope,
        RawFolderTree rawFolderTree)
    {
        if (!string.IsNullOrWhiteSpace(scope.SelectedFileId))
        {
            return node.Id == scope.SelectedFileId;
        }

        if (string.IsNullOrWhiteSpace(scope.StopAtFolderId))
        {
            return true;
        }

        return HasRawAncestor(node, scope.StopAtFolderId, rawFolderTree);
    }

    private static bool HasRawAncestor(
        RawFolderNodeInfo node,
        string stopAtFolderId,
        RawFolderTree rawFolderTree)
    {
        var parentId = node.ParentId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(parentId))
        {
            if (parentId == stopAtFolderId)
            {
                return true;
            }

            if (!visited.Add(parentId) || !rawFolderTree.NodesById.TryGetValue(parentId, out var parent))
            {
                return false;
            }

            parentId = parent.ParentId;
        }

        return false;
    }

    private static string? TryBuildFolderRelativePath(
        INode node,
        IReadOnlyDictionary<string, INode> nodesById,
        string? stopAtFolderId,
        IReadOnlyDictionary<string, string> decryptedNames,
        RawFolderTree rawFolderTree)
    {
        if (TryGetRawNodeInfo(node, rawFolderTree) is { } rawNode)
        {
            return TryBuildRawRelativePath(rawNode, rawFolderTree, stopAtFolderId);
        }

        return GetBestNodeName(node, decryptedNames) is null
            ? null
            : BuildRelativePath(node, nodesById, stopAtFolderId, decryptedNames);
    }

    private static string? TryBuildRawRelativePath(
        RawFolderNodeInfo node,
        RawFolderTree rawFolderTree,
        string? stopAtFolderId)
    {
        if (!IsUsableName(node.Name))
        {
            return null;
        }

        var segments = new Stack<string>();
        segments.Push(node.Name);

        var parentId = node.ParentId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(parentId))
        {
            if (!string.IsNullOrWhiteSpace(stopAtFolderId) && parentId == stopAtFolderId)
            {
                break;
            }

            if (!visited.Add(parentId))
            {
                return null;
            }

            if (!rawFolderTree.NodesById.TryGetValue(parentId, out var parent))
            {
                break;
            }

            if (!IsUsableName(parent.Name))
            {
                return null;
            }

            segments.Push(parent.Name);
            parentId = parent.ParentId;
        }

        return Path.Combine(segments.ToArray());
    }

    private static string BuildRelativePath(
        INode node,
        IReadOnlyDictionary<string, INode> nodesById,
        string? stopAtFolderId,
        IReadOnlyDictionary<string, string> decryptedNames)
    {
        var segments = new Stack<string>();
        segments.Push(GetSafeNodeName(node, decryptedNames));

        var parentId = node.ParentId;
        while (!string.IsNullOrWhiteSpace(parentId) && nodesById.TryGetValue(parentId, out var parent))
        {
            if (parent.Id == stopAtFolderId || parent.Type == NodeType.Root)
            {
                break;
            }
            else if (GetBestNodeName(parent, decryptedNames) is { } parentName)
            {
                segments.Push(parentName);
            }
            else
            {
                segments.Push(GetSafeNodeName(parent, decryptedNames));
            }

            parentId = parent.ParentId;
        }

        return Path.Combine(segments.ToArray());
    }

    private static string GetSafeNodeName(
        INode node,
        IReadOnlyDictionary<string, string> decryptedNames)
    {
        return GetBestNodeName(node, decryptedNames)
            ?? throw new MegaApiException("MEGA node name could not be decrypted.");
    }

    private static string GetPublicNodeHandle(INode node)
    {
        return node.Id;
    }

    private static string? GetNodeShareId(INode node)
    {
        return node.GetType().GetProperty("ShareId")?.GetValue(node) as string;
    }

    private static RawFolderNodeInfo? TryGetRawNodeInfo(INode node, RawFolderTree rawFolderTree)
    {
        if (!string.IsNullOrWhiteSpace(node.Id)
            && rawFolderTree.NodesById.TryGetValue(node.Id, out var rawNode))
        {
            return rawNode;
        }

        var shareId = GetNodeShareId(node);
        if (!string.IsNullOrWhiteSpace(shareId)
            && rawFolderTree.NodesById.TryGetValue(shareId, out rawNode))
        {
            return rawNode;
        }

        return null;
    }

    private static string GetFallbackFolderName(RawMegaNode node)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder("folder-");
        foreach (var character in node.Id)
        {
            builder.Append(invalidCharacters.Contains(character) ? '_' : character);
        }

        return builder.Length > "folder-".Length ? builder.ToString() : "folder";
    }

    private static bool IsUsableName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name)
            && !name.StartsWith("Attribute deserialization failed:", StringComparison.OrdinalIgnoreCase);       
    }

    private static string? GetBestNodeName(
        INode node,
        IReadOnlyDictionary<string, string> decryptedNames)
    {
        if (!string.IsNullOrWhiteSpace(node.Id)
            && decryptedNames.TryGetValue(node.Id, out var decryptedName)
            && IsUsableName(decryptedName))
        {
            return decryptedName;
        }

        var shareId = GetNodeShareId(node);
        if (!string.IsNullOrWhiteSpace(shareId)
            && decryptedNames.TryGetValue(shareId, out decryptedName)
            && IsUsableName(decryptedName))
        {
            return decryptedName;
        }

        if (IsUsableName(node.Name))
        {
            return node.Name;
        }

        foreach (var candidate in EnumerateNodeObjects(node))
        {
            var attributes = GetPropertyValue(candidate, "Attributes");
            var attributeName = GetStringPropertyValue(attributes, "Name");
            if (IsUsableName(attributeName))
            {
                return attributeName;
            }

            var serializedAttributes = GetStringPropertyValue(candidate, "SerializedAttributes");
            foreach (var key in EnumerateAttributeKeys(candidate))
            {
                var candidateName = TryDecryptNameFromSerializedAttributes(serializedAttributes, key);
                if (IsUsableName(candidateName))
                {
                    return candidateName;
                }
            }

            var plainSerializedName = TryExtractNameFromSerializedJson(serializedAttributes);
            if (IsUsableName(plainSerializedName))
            {
                return plainSerializedName;
            }
        }

        return null;
    }

    private static IEnumerable<object> EnumerateNodeObjects(INode node)
    {
        yield return node;

        var innerNode = node.GetType()
            .GetField("_node", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(node);
        if (innerNode is not null)
        {
            yield return innerNode;
        }
    }

    private static object? GetPropertyValue(object? instance, string propertyName)
    {
        return instance?.GetType()
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(instance);
    }

    private static string? GetStringPropertyValue(object? instance, string propertyName)
    {
        return GetPropertyValue(instance, propertyName) as string;
    }

    private static IEnumerable<byte[]> EnumerateAttributeKeys(object nodeLike)
    {
        var key = GetPropertyValue(nodeLike, "Key") as byte[];
        if (key is { Length: >= 16 })
        {
            yield return key.Take(16).ToArray();
        }

        var fullKey = GetPropertyValue(nodeLike, "FullKey") as byte[];
        if (fullKey is { Length: >= 16 })
        {
            yield return fullKey.Take(16).ToArray();
        }

        if (fullKey is { Length: >= 32 })
        {
            var xorKey = new byte[16];
            for (var i = 0; i < xorKey.Length; i++)
            {
                xorKey[i] = (byte)(fullKey[i] ^ fullKey[i + 16]);
            }

            yield return xorKey;
        }
    }

    private static string? TryDecryptNameFromSerializedAttributes(string? serializedAttributes, byte[] key)
    {
        if (string.IsNullOrWhiteSpace(serializedAttributes) || key.Length < 16)
        {
            return null;
        }

        try
        {
            var encrypted = DecodeBase64Url(serializedAttributes);
            if (encrypted.Length == 0 || encrypted.Length % 16 != 0)
            {
                return null;
            }

            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.Key = key.Take(16).ToArray();
            aes.IV = new byte[16];

            using var decryptor = aes.CreateDecryptor();
            var decrypted = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
            var text = Encoding.UTF8.GetString(decrypted).TrimEnd('\0');
            return TryExtractNameFromSerializedJson(text);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? TryExtractNameFromSerializedJson(string? serializedAttributes)
    {
        if (string.IsNullOrWhiteSpace(serializedAttributes))
        {
            return null;
        }

        var jsonStart = serializedAttributes.IndexOf('{');
        var jsonEnd = serializedAttributes.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd <= jsonStart)
        {
            return null;
        }

        try
        {
            var json = serializedAttributes[jsonStart..(jsonEnd + 1)];
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("n", out var nameProperty)
                && nameProperty.ValueKind == JsonValueKind.String
                    ? nameProperty.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string> GetPublicFileDownloadUrlAsync(MegaLink fileLink, CancellationToken cancellationToken)
    {
        var request = new MegaDownloadRequest(FilePublicHandle: fileLink.NodeId);
        return await SendDownloadUrlRequestAsync(request, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetPublicFolderFileDownloadUrlAsync(MegaFolderFile folderFile, CancellationToken cancellationToken)
    {
        var request = new MegaDownloadRequest(NodeId: GetPublicNodeHandle(folderFile.Node));
        return await SendDownloadUrlRequestAsync(request, folderFile.FolderLink.NodeId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SendDownloadUrlRequestAsync(
        MegaDownloadRequest request,
        string? publicFolderId,
        CancellationToken cancellationToken)
    {
        await EnsureAccountLoggedInIfConfiguredAsync(cancellationToken).ConfigureAwait(false);

        var requestId = Interlocked.Increment(ref _apiRequestId);
        var endpoint = BuildApiEndpoint(requestId, publicFolderId);

        var payload = JsonSerializer.Serialize(new[] { request });
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var response = await _apiHttpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
        {
            throw new MegaApiException("MEGA returned an empty download response.");
        }

        var first = document.RootElement[0];
        if (first.ValueKind == JsonValueKind.Number && first.TryGetInt32(out var apiError))
        {
            throw new MegaApiException(FormatApiError(apiError));
        }

        if (first.ValueKind == JsonValueKind.Object
            && first.TryGetProperty("g", out var urlProperty)
            && urlProperty.ValueKind == JsonValueKind.String)
        {
            var url = urlProperty.GetString();
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        throw new MegaApiException("MEGA did not return a usable download URL.");
    }

    private string BuildApiEndpoint(long requestId, string? publicFolderId = null)
    {
        var endpoint = new StringBuilder($"https://g.api.mega.co.nz/cs?id={requestId}");
        if (_loginMode == LoginMode.Account && _accountSession is { } accountSession)
        {
            endpoint.Append("&sid=");
            endpoint.Append(Uri.EscapeDataString(accountSession.SessionId));
        }

        if (!string.IsNullOrWhiteSpace(publicFolderId))
        {
            endpoint.Append("&n=");
            endpoint.Append(Uri.EscapeDataString(publicFolderId));
        }

        return endpoint.ToString();
    }

    private static byte[] BuildEngineKey(MegaLink sourceLink, INode node)
    {
        var nodeType = node.GetType();
        var aesKey = nodeType.GetProperty("Key")?.GetValue(node) as byte[];
        var iv = nodeType.GetProperty("Iv")?.GetValue(node) as byte[];
        if (aesKey is { Length: 16 } && iv is { Length: >= 8 })
        {
            return [.. aesKey, .. iv.Take(8)];
        }

        var fullKey = nodeType.GetProperty("FullKey")?.GetValue(node) as byte[];
        if (fullKey is { Length: >= 32 })
        {
            return BuildEngineKeyFromFullKey(fullKey);
        }

        return BuildEngineKeyFromFullKey(DecodeBase64Url(sourceLink.Key));
    }

    private static byte[] BuildEngineKeyFromFullKey(byte[] fullKey)
    {
        if (fullKey.Length >= 32)
        {
            var aesKey = new byte[16];
            for (var i = 0; i < aesKey.Length; i++)
            {
                aesKey[i] = (byte)(fullKey[i] ^ fullKey[i + 16]);
            }

            return [.. aesKey, .. fullKey.Skip(16).Take(8)];
        }

        if (fullKey.Length >= 24)
        {
            return fullKey.Take(24).ToArray();
        }

        throw new MegaApiException("MEGA file key is incomplete.");
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        return Convert.FromBase64String(normalized);
    }

    private static string FormatApiError(int apiError)
    {
        return apiError switch
        {
            -1 => "MEGA API response: Internal error.",
            -2 => "MEGA API response: Bad arguments.",
            -3 => "MEGA API response: Temporary failure, retry later.",
            -4 => "MEGA API response: Too many requests.",
            -9 => "MEGA API response: File or folder does not exist.",
            -11 => "MEGA API response: AccessDenied. The public folder context was rejected or the link is no longer accessible.",
            -14 => "MEGA API response: Invalid file key.",
            -16 => "MEGA API response: Resource blocked.",
            -17 => "MEGA API response: Quota exceeded.",
            -18 => "MEGA API response: Resource temporarily unavailable.",
            -19 => "MEGA API response: Too many connections.",
            _ => $"MEGA API response: error {apiError}."
        };
    }

    private sealed record MegaDownloadRequest(
        [property: JsonPropertyName("a")] string Action = "g",
        [property: JsonPropertyName("g")] int IncludeDownloadUrl = 1,
        [property: JsonPropertyName("p")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? FilePublicHandle = null,
        [property: JsonPropertyName("n")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? NodeId = null);

    private sealed record MegaFolderNodesRequest(
        [property: JsonPropertyName("a")] string Action = "f",
        [property: JsonPropertyName("c")] int Children = 1,
        [property: JsonPropertyName("ca")] int IncludeAttributes = 1,
        [property: JsonPropertyName("r")] int Recursive = 1);

    private enum LoginMode
    {
        None,
        Anonymous,
        Account
    }

    private sealed record RawFolderTree(
        IReadOnlyDictionary<string, RawFolderNodeInfo> NodesById,
        IReadOnlyDictionary<string, string> NamesById)
    {
        public static RawFolderTree Empty { get; } = new(
            new Dictionary<string, RawFolderNodeInfo>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private sealed record RawFolderNodeInfo(
        string Id,
        string? ParentId,
        int Type,
        string Name,
        long SizeBytes,
        byte[] FullKey,
        bool HasDecryptedName);

    private sealed record RawMegaNode(
        string Id,
        string? ParentId,
        int Type,
        long SizeBytes,
        string? SerializedAttributes,
        string? SerializedKey)
    {
        public static RawMegaNode? From(JsonElement element)
        {
            if (!TryGetString(element, "h", out var id) || string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            if (!element.TryGetProperty("t", out var typeProperty) || !typeProperty.TryGetInt32(out var type))
            {
                return null;
            }

            TryGetString(element, "p", out var parentId);
            TryGetString(element, "a", out var serializedAttributes);
            TryGetString(element, "k", out var serializedKey);
            var sizeBytes = element.TryGetProperty("s", out var sizeProperty)
                && sizeProperty.TryGetInt64(out var parsedSize)
                    ? parsedSize
                    : 0;
            return new RawMegaNode(id, parentId, type, sizeBytes, serializedAttributes, serializedKey);
        }

        private static bool TryGetString(JsonElement element, string propertyName, out string? value)
        {
            value = null;
            if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = property.GetString();
            return true;
        }
    }

    private sealed record FolderScope(
        string FolderName,
        string? StopAtFolderId,
        string? SelectedFileId);
}

public sealed record MegaAccountSession(
    string Email,
    string SessionId,
    string MasterKeyBase64);
