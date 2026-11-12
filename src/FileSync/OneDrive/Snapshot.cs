using System.Security.Cryptography;
using System.Text.Json;
using FileSync.Core;

namespace FileSync.OneDrive;

public sealed record SnapshotHead(string SnapshotId, string Hash);
public sealed record Snapshot(string Id, Manifest Manifest, IReadOnlyDictionary<string, string> DirectoryHashes)
{
    public const int CurrentVersion = 1;
    public int Version { get; init; } = CurrentVersion;
    public string Publisher { get; init; } = "";
    public DateTime PublishedUtc { get; init; }
}

public static class SnapshotMetadata
{
    private const int HashLength = 64;
    private static readonly long MinimumFileTime = new DateTime(1601, 1, 1).Ticks;

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static bool ValidHash(string? value) => value?.Length == HashLength &&
        value.All(c => char.IsAsciiHexDigit(c) && !char.IsAsciiLetterLower(c));

    public static void Validate(Snapshot snapshot, string target)
    {
        if (snapshot.Version != Snapshot.CurrentVersion) throw new UserError("OneDrive 清单版本不受支持。");
        StateStore.SafeId(snapshot.Id);
        FileScanner.Validate(snapshot.Manifest, target);
        SyncScope.Validate(snapshot.Manifest);
        if (!ValidTimes(snapshot.Manifest.RootCreatedTicks, snapshot.Manifest.RootModifiedTicks) ||
            snapshot.Manifest.Entries.Any(e => !ValidTimes(e.CreatedTicks, e.ModifiedTicks)))
            throw new UserError("OneDrive 清单缺少有效的原始创建或修改时间。");
        if (snapshot.Manifest.Entries.Any(e => !e.Directory && !ValidHash(e.Hash)))
            throw new UserError("OneDrive 清单文件哈希无效。");
        var expected = DirectoryHashes(snapshot.Manifest);
        if (snapshot.DirectoryHashes is null || expected.Count != snapshot.DirectoryHashes.Count ||
            expected.Any(pair => !snapshot.DirectoryHashes.TryGetValue(pair.Key, out var hash) || hash != pair.Value))
            throw new UserError("OneDrive 目录哈希校验失败。", 409);
    }

    private static bool ValidTimes(long? created, long modified) => created >= MinimumFileTime &&
        created <= DateTime.MaxValue.Ticks && modified >= MinimumFileTime && modified <= DateTime.MaxValue.Ticks;

    public static IReadOnlyDictionary<string, string> DirectoryHashes(Manifest manifest)
    {
        var children = manifest.Entries.ToLookup(e => Parent(e.Path), StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries.Where(e => e.Directory).OrderByDescending(e => e.Path.Length))
            result.Add(entry.Path, TreeHash(entry, children[entry.Path], result));
        var root = new Entry("", true, 0, manifest.RootModifiedTicks, null)
            { CreatedTicks = manifest.RootCreatedTicks };
        result.Add("", TreeHash(root, children[""], result));
        return result;
    }

    private static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private static string TreeHash(Entry directory, IEnumerable<Entry> children, IReadOnlyDictionary<string, string> hashes)
    {
        var entries = children.OrderBy(e => e.Path, StringComparer.Ordinal)
            .Select(e => e.Directory ? e with { Hash = hashes[e.Path] } : e).ToArray();
        return Hash(JsonSerializer.SerializeToUtf8Bytes(new { directory, entries }, JsonFiles.Options));
    }
}
