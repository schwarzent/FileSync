using System.Security.Cryptography;
using System.Text;
using FileSync.Core;
using FileSync.Sync;

namespace FileSync.OneDrive;

public sealed record PublicationState(SnapshotHead? Previous, SnapshotHead Next, Manifest Before);
public sealed record PublicationRecovery(Profile Profile, PublicationState Update, FileScanner Scanner);

public static class RepositoryWorkspace
{
    public const string PendingFile = "publishing.json";

    public static void AssertReady(Profile profile)
    {
        if (File.Exists(OneDriveFiles.Metadata(profile, PendingFile)))
            throw new UserError("仓库发布尚未完成。请等待 OneDrive 同步；发布设备中断时，可在仓库历史中恢复发布。", 409);
    }

    public static async Task<Manifest> Scan(Profile profile, FileScanner scanner, CancellationToken token)
    {
        var root = OneDriveFiles.Relay(profile);
        Directory.CreateDirectory(root);
        return RepositoryRules.Normalize(await scanner.ScanMetadata(root, token, RepositoryRules.MetadataPattern));
    }

    public static void AssertContent(Manifest actual, Manifest expected)
    {
        if (!actual.Entries.Select(Content).SequenceEqual(expected.Entries.Select(Content)))
            throw new UserError("仓库普通文件与提交清单不同，可能尚未同步完整或被直接修改，请检查 OneDrive 状态及仓库文件。", 409);
    }

    private static object Content(Entry entry) => new { entry.Path, entry.Directory, entry.Length, entry.Hash };

    public static async Task<Snapshot> Recover(Profile profile, FileScanner scanner, CancellationToken token)
    {
        var path = OneDriveFiles.Metadata(profile, PendingFile);
        if (!File.Exists(path)) throw new UserError("此仓库没有中断的发布。", 409);
        var update = JsonFiles.Read<PublicationState>(path);
        return await Finish(new(profile, update, scanner), token);
    }

    public static async Task<Snapshot> Finish(PublicationRecovery recovery, CancellationToken token)
    {
        var (profile, update, scanner) = recovery;
        FileScanner.Validate(update.Before, profile.TargetPath);
        RepositoryRules.ValidatePaths(update.Before);
        var current = OneDriveFiles.Head(profile);
        if (current != update.Previous && current != update.Next)
            throw new UserError("仓库已收到另一条提交，不能恢复此发布。请保留中断记录。", 409);
        var snapshot = OneDriveFiles.ReadCommit(profile, update.Next);
        if (snapshot.Parent != update.Previous) throw new UserError("发布恢复记录的父提交不匹配。", 409);
        await OneDriveFiles.VerifyObjects(profile, snapshot.Manifest, token);
        await AssertRecoverable(new(profile, update, snapshot.Manifest) { Scanner = scanner }, token);
        Archive(profile, update, snapshot.Manifest);
        await Install(profile, snapshot, token);
        AssertContent(await Scan(profile, scanner, token), snapshot.Manifest);
        var history = RepositoryHistory.Read(profile, update.Next);
        JsonFiles.Write(OneDriveFiles.Metadata(profile, "history.json"), history, RepositoryHistory.JsonOptions);
        if (OneDriveFiles.Head(profile) != current)
            throw new UserError("完成发布前发现其他提交，停止更新仓库记录。", 409);
        JsonFiles.Write(OneDriveFiles.Metadata(profile, "current.json"), update.Next);
        var completed = OneDriveFiles.Metadata(profile, "completed-publications/" + snapshot.Id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(completed)!);
        File.Move(OneDriveFiles.Metadata(profile, PendingFile), completed);
        return snapshot;
    }

    private sealed record RecoveryCheck(Profile Profile, PublicationState Update, Manifest Desired)
    {
        public required FileScanner Scanner { get; init; }
    }

    private static async Task AssertRecoverable(RecoveryCheck check, CancellationToken token)
    {
        var (profile, update, desired) = check;
        var actual = await Scan(profile, check.Scanner, token);
        var allowed = update.Before.Entries.Concat(desired.Entries).ToLookup(e => e.Path, StringComparer.Ordinal);
        if (actual.Entries.Any(e => !allowed[e.Path].Any(candidate => Content(candidate).Equals(Content(e)))))
            throw new UserError("发布期间仓库出现清单之外的修改，停止恢复以保护这些文件。", 409);
        var index = actual.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in update.Before.Entries)
        {
            if (index.ContainsKey(entry.Path) || Exists(ArchivePath(profile, update, entry))) continue;
            throw new UserError($"发布恢复缺少原始文件或归档：{entry.Path}", 409);
        }
    }

    private static string ArchivePath(Profile profile, PublicationState update, Entry entry)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Path)));
        return OneDriveFiles.Metadata(profile, "replaced/" + update.Next.SnapshotId + "/" + key);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void Archive(Profile profile, PublicationState update, Manifest desired)
    {
        var after = desired.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        foreach (var entry in update.Before.Entries.OrderByDescending(e => e.Path.Length))
        {
            if (after.TryGetValue(entry.Path, out var next) && entry.Directory == next.Directory &&
                (entry.Directory || entry.Hash == next.Hash)) continue;
            var destination = ArchivePath(profile, update, entry);
            if (Exists(destination)) continue;
            var source = PathPolicy.Resolve(OneDriveFiles.Relay(profile), entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (entry.Directory) Directory.Move(source, destination);
            else File.Move(source, destination);
        }
    }

    private static async Task Install(Profile profile, Snapshot snapshot, CancellationToken token)
    {
        var root = OneDriveFiles.Relay(profile);
        foreach (var entry in snapshot.Manifest.Entries.Where(e => e.Directory).OrderBy(e => e.Path.Length))
            Directory.CreateDirectory(PathPolicy.Resolve(root, entry.Path));
        foreach (var entry in snapshot.Manifest.Entries.Where(e => !e.Directory))
        {
            token.ThrowIfCancellationRequested();
            var destination = PathPolicy.Resolve(root, entry.Path);
            if (File.Exists(destination)) { await TransactionFiles.VerifyFile(destination, entry, token); continue; }
            var temporary = OneDriveFiles.Metadata(profile, "materializing/" + snapshot.Id + "/" + entry.Hash);
            Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
            await using (var source = FileScanner.OpenRead(OneDriveFiles.Blob(profile, entry.Hash!)))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                             FileScanner.BufferSize, FileOptions.Asynchronous))
            {
                await source.CopyToAsync(output, token);
                await output.FlushAsync(token);
                output.Flush(true);
            }
            await TransactionFiles.VerifyFile(temporary, entry, token);
            File.Move(temporary, destination);
        }
        TransactionFiles.RestoreTimes(root, snapshot.Manifest);
    }
}
