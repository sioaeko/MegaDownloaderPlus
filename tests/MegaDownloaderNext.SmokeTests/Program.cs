using MegaDownloaderNext.Core.Links;
using MegaDownloaderNext.Core.Downloads;
using MegaDownloaderNext.Core.Mega;
using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

var cases = new (string Url, MegaLinkKind Kind, string NodeId, string Key, string? SelectedNodeId, MegaLinkKind? SelectedNodeKind)[]
{
    ("https://mega.nz/file/abcDEF_123#key-456_789", MegaLinkKind.File, "abcDEF_123", "key-456_789", null, null),
    ("https://mega.nz/folder/FOLDERID#FOLDERKEY", MegaLinkKind.Folder, "FOLDERID", "FOLDERKEY", null, null),
    ("https://mega.nz/folder/FOLDERID#FOLDERKEY/folder/SUBFOLDER", MegaLinkKind.Folder, "FOLDERID", "FOLDERKEY", "SUBFOLDER", MegaLinkKind.Folder),
    ("https://mega.nz/folder/FOLDERID#FOLDERKEY/folder/PARENT/folder/CHILD", MegaLinkKind.Folder, "FOLDERID", "FOLDERKEY", "CHILD", MegaLinkKind.Folder),
    ("https://mega.nz/folder/FOLDERID#FOLDERKEY/file/SUBFILE", MegaLinkKind.Folder, "FOLDERID", "FOLDERKEY", "SUBFILE", MegaLinkKind.File),
    ("https://mega.nz/folder/FOLDERID#FOLDERKEY/folder/SUBFOLDER?e=invite", MegaLinkKind.Folder, "FOLDERID", "FOLDERKEY", "SUBFOLDER", MegaLinkKind.Folder),
    ("https://www.mega.nz/file/abcDEF_123#key-456_789?download=1", MegaLinkKind.File, "abcDEF_123", "key-456_789", null, null),
    ("https://mega.app/folder/FOLDERID#FOLDERKEY", MegaLinkKind.Folder, "FOLDERID", "FOLDERKEY", null, null),
    ("mega.nz/folder/FOLDERID#FOLDERKEY", MegaLinkKind.Folder, "FOLDERID", "FOLDERKEY", null, null),
    ("https://mega.nz/#!legacyId!legacyKey", MegaLinkKind.File, "legacyId", "legacyKey", null, null),
    ("https://mega.co.nz/#F!folderId!folderKey", MegaLinkKind.Folder, "folderId", "folderKey", null, null),
    ("mega://#!schemeId!schemeKey", MegaLinkKind.File, "schemeId", "schemeKey", null, null),
    ("mega://#F!schemeFolder!schemeFolderKey", MegaLinkKind.Folder, "schemeFolder", "schemeFolderKey", null, null)
};

foreach (var (url, kind, nodeId, key, selectedNodeId, selectedNodeKind) in cases)
{
    if (!MegaUrlParser.TryParse(url, out var link))
    {
        Fail($"Expected valid link: {url}");
    }

    Equal(kind, link.Kind, "kind");
    Equal(nodeId, link.NodeId, "node id");
    Equal(key, link.Key, "key");
    Equal(selectedNodeId, link.SelectedNodeId, "selected node id");
    Equal(selectedNodeKind, link.SelectedNodeKind, "selected node kind");
}

if (MegaUrlParser.TryParse("https://example.com/not-mega", out _))
{
    Fail("Non-MEGA link should not parse.");
}

var many = MegaUrlParser.ParseMany("""
    paste:
    https://mega.nz/file/one#two
    invalid
    https://mega.nz/folder/three#four
    <https://mega.nz/folder/five#six/folder/seven?e=test>,
    (mega.nz/file/eight#nine)
    """);

Equal(4, many.Count, "parse many count");
Equal("seven", many[2].SelectedNodeId, "parse many selected node");
Equal("eight", many[3].NodeId, "parse many no-scheme node");

var queue = new DownloadQueue();
var queued = queue.Add(many[0], @"C:\Downloads");
Equal(MegaLinkKind.File, queued.Kind, "queued file kind");
Equal(DownloadState.Queued, queued.State, "queued state");
Equal(true, queue.ContainsEquivalent(many[0], @"C:\Downloads\"), "duplicate link same target");
Equal(false, queue.ContainsEquivalent(many[1], @"C:\Downloads"), "different link not duplicate");
Equal(false, queue.ContainsEquivalent(many[0], @"C:\OtherDownloads"), "same link different target allowed");

var folderLink = new MegaLink(MegaLinkKind.Folder, "folder", "folderKey", "https://mega.nz/folder/folder#folderKey");
var folderNode = new MegaFileNodeSnapshot("nodeA", "child.bin", 123, "folder", BytesFrom(120, 32));
var folderFile = new MegaFolderFile(folderLink, folderNode, Path.Combine("sub", "child.bin"));
var folderQueue = new DownloadQueue();
folderQueue.Add(folderFile, @"C:\Downloads\Folder");
Equal(true, folderQueue.ContainsEquivalent(folderFile, @"C:\Downloads\Folder\"), "duplicate folder file same target");

VerifyRawFolderExpansionKeepsChildrenWhenFolderNameIsMissing();
VerifyMegaAccountSessionConfiguration();

Console.WriteLine("Smoke tests passed.");

static void VerifyMegaAccountSessionConfiguration()
{
    using var service = new MegaApiTransferService();
    var session = new MegaAccountSession(
        " user@example.com ",
        "fake-session",
        Convert.ToBase64String(BytesFrom(80, 16)));

    service.ConfigureAccountSession(session);
    service.ClearAccountSession();

    try
    {
        service.ConfigureAccountSession(new MegaAccountSession(" ", "fake-session", Convert.ToBase64String(BytesFrom(100, 16))));
        Fail("Empty MEGA account email should be rejected.");
    }
    catch (ArgumentException)
    {
    }
}

static void VerifyRawFolderExpansionKeepsChildrenWhenFolderNameIsMissing()
{
    var serviceType = typeof(MegaApiTransferService);
    var rawNodeType = serviceType.GetNestedType("RawMegaNode", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("RawMegaNode type was not found.");
    var decryptMethod = serviceType.GetMethod("DecryptRawFolderTree", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DecryptRawFolderTree method was not found.");
    var pathMethod = serviceType.GetMethod("TryBuildRawRelativePath", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("TryBuildRawRelativePath method was not found.");

    var sharedFolderKey = BytesFrom(1, 16);
    var childFolderKey = BytesFrom(17, 16);
    var fileFullKey = BytesFrom(33, 32);

    var nodes = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(rawNodeType))!;
    nodes.Add(CreateRawMegaNode(
        rawNodeType,
        "root",
        null,
        1,
        0,
        EncryptAttributes("{\"n\":\"Root\"}", sharedFolderKey),
        null));
    nodes.Add(CreateRawMegaNode(
        rawNodeType,
        "folderA",
        "root",
        1,
        0,
        null,
        ToBase64Url(EncryptAesEcb(childFolderKey, sharedFolderKey))));
    nodes.Add(CreateRawMegaNode(
        rawNodeType,
        "fileA",
        "folderA",
        0,
        123,
        EncryptAttributes("{\"n\":\"child.txt\"}", BuildFileAttributeKey(fileFullKey)),
        ToBase64Url(EncryptAesEcb(fileFullKey, childFolderKey))));

    var tree = decryptMethod.Invoke(null, new object?[] { nodes, sharedFolderKey, "root" })
        ?? throw new InvalidOperationException("Raw folder tree was not returned.");
    var nodesById = (IDictionary)(tree.GetType().GetProperty("NodesById")?.GetValue(tree)
        ?? throw new InvalidOperationException("NodesById was not returned."));

    Equal(3, nodesById.Count, "raw fallback node count");

    var folderInfo = nodesById["folderA"] ?? throw new InvalidOperationException("Fallback folder was not retained.");
    var folderName = (string)(folderInfo.GetType().GetProperty("Name")?.GetValue(folderInfo)
        ?? throw new InvalidOperationException("Fallback folder name was not retained."));
    var hasDecryptedName = (bool)(folderInfo.GetType().GetProperty("HasDecryptedName")?.GetValue(folderInfo)
        ?? throw new InvalidOperationException("Fallback name marker was not retained."));
    Equal("folder-folderA", folderName, "fallback folder name");
    Equal(false, hasDecryptedName, "fallback folder decrypted-name marker");

    var fileInfo = nodesById["fileA"] ?? throw new InvalidOperationException("Child file was not decrypted.");
    var relativePath = (string?)pathMethod.Invoke(null, new object?[] { fileInfo, tree, "root" });
    Equal(Path.Combine("folder-folderA", "child.txt"), relativePath, "raw fallback relative path");
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        Fail($"Expected {label} '{expected}', got '{actual}'.");
    }
}

static void Fail(string message)
{
    Console.Error.WriteLine(message);
    Environment.Exit(1);
}

static object CreateRawMegaNode(
    Type rawNodeType,
    string id,
    string? parentId,
    int type,
    long sizeBytes,
    string? serializedAttributes,
    string? serializedKey)
{
    return Activator.CreateInstance(
        rawNodeType,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        args: new object?[] { id, parentId, type, sizeBytes, serializedAttributes, serializedKey },
        culture: null)
        ?? throw new InvalidOperationException("RawMegaNode could not be created.");
}

static byte[] BytesFrom(int start, int count)
{
    var bytes = new byte[count];
    for (var i = 0; i < bytes.Length; i++)
    {
        bytes[i] = (byte)(start + i);
    }

    return bytes;
}

static string EncryptAttributes(string json, byte[] key)
{
    using var aes = Aes.Create();
    aes.Mode = CipherMode.CBC;
    aes.Padding = PaddingMode.None;
    aes.Key = key.Take(16).ToArray();
    aes.IV = new byte[16];

    using var encryptor = aes.CreateEncryptor();
    var plainText = PadToBlock(Encoding.UTF8.GetBytes(json));
    return ToBase64Url(encryptor.TransformFinalBlock(plainText, 0, plainText.Length));
}

static byte[] EncryptAesEcb(byte[] plainText, byte[] key)
{
    using var aes = Aes.Create();
    aes.Mode = CipherMode.ECB;
    aes.Padding = PaddingMode.None;
    aes.Key = key.Take(16).ToArray();

    using var encryptor = aes.CreateEncryptor();
    var padded = PadToBlock(plainText);
    return encryptor.TransformFinalBlock(padded, 0, padded.Length);
}

static byte[] BuildFileAttributeKey(byte[] fullKey)
{
    var key = new byte[16];
    for (var i = 0; i < key.Length; i++)
    {
        key[i] = (byte)(fullKey[i] ^ fullKey[i + 16]);
    }

    return key;
}

static byte[] PadToBlock(byte[] bytes)
{
    var paddedLength = ((bytes.Length + 15) / 16) * 16;
    var padded = new byte[paddedLength];
    Array.Copy(bytes, padded, bytes.Length);
    return padded;
}

static string ToBase64Url(byte[] bytes)
{
    return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
