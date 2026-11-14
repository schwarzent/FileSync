using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using FileSync.Core;
using FileSync.Sync;

namespace FileSync.OneDrive;

public sealed record RepositoryFile(string Path, bool Directory, long Length)
{
    public DateTime? CreatedUtc { get; init; }
    public DateTime ModifiedUtc { get; init; }
    public string? Hash { get; init; }
}

public sealed record RepositoryChange(string Kind, string Path)
{
    public RepositoryFile? Before { get; init; }
    public RepositoryFile? After { get; init; }
}

public sealed record RepositoryCommit(string Id, long Sequence, DateTime UploadedUtc)
{
    public required string Publisher { get; init; }
    public string? ParentId { get; init; }
    public DateTime? RootCreatedUtc { get; init; }
    public DateTime RootModifiedUtc { get; init; }
    public required IReadOnlyList<RepositoryChange> Changes { get; init; }
}

public sealed record RepositoryContent(string Kind, string? Text);
public sealed record RepositoryComparison(RepositoryChange Change, RepositoryContent? Before, RepositoryContent? After);
public sealed record RepositoryComparisonRequest(Profile Profile, string CommitId, int ChangeIndex);

public static class RepositoryHistory
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonFiles.Options)
        { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    public static IReadOnlyList<RepositoryCommit> Read(Profile profile, SnapshotHead? head = null)
    {
        var chain = Chain(profile, head ?? OneDriveFiles.Head(profile));
        return chain.Select((snapshot, index) => Describe(snapshot, chain.ElementAtOrDefault(index + 1))).ToArray();
    }

    private static IReadOnlyList<Snapshot> Chain(Profile profile, SnapshotHead? head)
    {
        var chain = new List<Snapshot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (head is not null)
        {
            if (!seen.Add(head.SnapshotId)) throw new UserError("仓库提交历史存在循环。", 409);
            var snapshot = OneDriveFiles.ReadCommit(profile, head);
            if (chain.Count > 0 && chain[^1].Sequence != snapshot.Sequence + 1)
                throw new UserError("仓库提交顺序不连续。", 409);
            chain.Add(snapshot);
            head = snapshot.Parent;
        }
        if (chain.Count > 0 && chain[^1].Sequence != 1) throw new UserError("仓库缺少初始提交。", 409);
        for (var index = 0; index < chain.Count; index++)
        {
            var snapshot = chain[index];
            var before = chain.ElementAtOrDefault(index + 1)?.Manifest ?? snapshot.Manifest with { Entries = [] };
            if (!Planner.Compare(before, snapshot.Manifest).SequenceEqual(snapshot.Changes))
                throw new UserError("仓库修改记录与前后清单不一致。", 409);
        }
        return chain;
    }

    private static RepositoryCommit Describe(Snapshot snapshot, Snapshot? parent) =>
        new(snapshot.Id, snapshot.Sequence, snapshot.PublishedUtc)
        {
            Publisher = snapshot.Publisher, ParentId = snapshot.Parent?.SnapshotId,
            RootCreatedUtc = Time(snapshot.Manifest.RootCreatedTicks),
            RootModifiedUtc = new(snapshot.Manifest.RootModifiedTicks, DateTimeKind.Utc),
            Changes = snapshot.Changes.Select(c => new RepositoryChange(c.Kind, c.Path)
            {
                Before = Facts(c.Before, parent), After = Facts(c.After, snapshot)
            }).ToArray()
        };

    private static DateTime? Time(long? ticks) => ticks is long value ? new(value, DateTimeKind.Utc) : null;

    private static RepositoryFile? Facts(Entry? entry, Snapshot? snapshot) => entry is null ? null :
        new(entry.Path, entry.Directory, entry.Length)
        {
            CreatedUtc = Time(entry.CreatedTicks), ModifiedUtc = new(entry.ModifiedTicks, DateTimeKind.Utc),
            Hash = entry.Directory ? snapshot?.DirectoryHashes[entry.Path] : entry.Hash
        };

    public static async Task<RepositoryComparison> Compare(RepositoryComparisonRequest request, CancellationToken token)
    {
        var (profile, id, index) = request;
        StateStore.SafeId(id);
        var commit = Read(profile).FirstOrDefault(c => c.Id == id) ?? throw new UserError("仓库提交不存在。", 404);
        if (index < 0 || index >= commit.Changes.Count) throw new UserError("文件修改记录不存在。", 404);
        var change = commit.Changes[index];
        return new(change, await Content(profile, change.Before, token), await Content(profile, change.After, token));
    }

    private static async Task<RepositoryContent?> Content(Profile profile, RepositoryFile? file, CancellationToken token)
    {
        if (file is null) return null;
        if (file.Directory) return new("directory", null);
        var path = OneDriveFiles.Blob(profile, file.Hash!);
        var entry = new Entry(file.Path, false, file.Length, file.ModifiedUtc.Ticks, file.Hash);
        await TransactionFiles.VerifyFile(path, entry, token);
        try
        {
            await using var stream = FileScanner.OpenRead(path);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false);
            var buffer = new char[FileScanner.BufferSize];
            var text = new StringBuilder();
            int count;
            while ((count = await reader.ReadAsync(buffer, token)) > 0)
            {
                if (buffer.AsSpan(0, count).Contains('\0')) return new("binary", null);
                text.Append(buffer, 0, count);
            }
            return new("text", text.ToString().TrimStart('\uFEFF'));
        }
        catch (DecoderFallbackException) { return new("binary", null); }
    }
}
