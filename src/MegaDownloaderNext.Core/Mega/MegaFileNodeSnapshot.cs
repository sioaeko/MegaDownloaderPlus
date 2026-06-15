using CG.Web.MegaApiClient;
using System.Reflection;

namespace MegaDownloaderNext.Core.Mega;

public sealed class MegaFileNodeSnapshot : INode
{
    public MegaFileNodeSnapshot(string id, string name, long size, string? parentId, byte[] fullKey)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("MEGA node id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("MEGA node name is required.", nameof(name));
        }

        if (fullKey.Length == 0)
        {
            throw new ArgumentException("MEGA node key is required.", nameof(fullKey));
        }

        Id = id;
        Name = name;
        Size = size;
        ParentId = parentId;
        FullKey = fullKey.ToArray();
    }

    public string Id { get; }

    public NodeType Type => NodeType.File;

    public string Name { get; }

    public long Size { get; }

    public DateTime? ModificationDate => null;

    public string Fingerprint => string.Empty;

    public string? ParentId { get; }

    public DateTime? CreationDate => null;

    public string Owner => string.Empty;

    public IFileAttribute[] FileAttributes => [];

    public byte[] FullKey { get; }

    public static MegaFileNodeSnapshot From(INode node, string? name = null)
    {
        var fullKey = node.GetType()
            .GetProperty("FullKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(node) as byte[];
        if (fullKey is null)
        {
            throw new ArgumentException("MEGA node does not expose a full key.", nameof(node));
        }

        return new MegaFileNodeSnapshot(
            node.Id,
            string.IsNullOrWhiteSpace(name) ? node.Name : name,
            node.Size,
            node.ParentId,
            fullKey);
    }

    public bool Equals(INode? other)
    {
        return other is not null && string.Equals(Id, other.Id, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj)
    {
        return obj is INode other && Equals(other);
    }

    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(Id);
    }
}
