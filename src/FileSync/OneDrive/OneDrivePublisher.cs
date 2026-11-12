using System.Text.Json;
using FileSync.Core;
using FileSync.Sync;

namespace FileSync.OneDrive;

public sealed record PublishResult(string SnapshotId, int Files, int Directories)
{
    public string Message => "已写入本机 OneDrive 中继，等待 OneDrive 客户端上传；另一端下载校验后才能确认同步完成。";
}

public sealed class OneDrivePublisher(StateStore store, FileScanner scanner, OneDriveFiles relay)
{
    public async Task<PublishResult> Publish(Profile profile, CancellationToken token, Action<SyncProgress> report)
    {
        var head = OneDriveFiles.Head(profile);
        var manifest = await scanner.ScanMetadata(profile.TargetPath, token, profile.IgnorePatterns);
        var desired = head is null ? manifest : Merge(profile, manifest, await relay.Manifest(profile, token));
        var snapshot = new Snapshot(StateStore.NewId(), desired, SnapshotMetadata.DirectoryHashes(desired))
            { Publisher = store.Current.Name, PublishedUtc = DateTime.UtcNow };
        SnapshotMetadata.Validate(snapshot, profile.TargetPath);
        await PublishContent(new(profile, snapshot, report), token);
        if (!FileScanner.Equal(manifest, await scanner.ScanMetadata(profile.TargetPath, token, profile.IgnorePatterns)))
            throw new UserError("发布期间本机文件或时间已改变，请重新发布。", 409);
        token.ThrowIfCancellationRequested();
        if (OneDriveFiles.Head(profile) != head) throw new UserError("发布期间 OneDrive 收到了其他发布记录，请重新检查后发布。", 409);
        Commit(profile, snapshot);
        return new(snapshot.Id, desired.Entries.Count(e => !e.Directory), desired.Entries.Count(e => e.Directory));
    }

    private Manifest Merge(Profile profile, Manifest local, Manifest remote)
    {
        var plan = LatestPlanner.Merge(new(StateStore.NewId(), profile, local,
            SyncScope.Filter(remote, profile.IgnorePatterns), [], DateTime.UtcNow));
        return PublicationMerge.Resolve(store, plan).Desired;
    }

    private sealed record Publication(Profile Profile, Snapshot Snapshot, Action<SyncProgress> Report);

    private static async Task PublishContent(Publication publication, CancellationToken token)
    {
        var entries = publication.Snapshot.Manifest.Entries.Where(e => !e.Directory).DistinctBy(e => e.Hash).ToArray();
        var total = entries.Sum(e => e.Length);
        long transferred = 0;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            publication.Report(new("uploading", entry.Path, transferred, total));
            await PublishFile(publication, entry, token);
            transferred += entry.Length;
            publication.Report(new("uploading", entry.Path, transferred, total));
        }
    }

    private static async Task PublishFile(Publication publication, Entry entry, CancellationToken token)
    {
        var destination = OneDriveFiles.Blob(publication.Profile, entry.Hash!);
        if (File.Exists(destination)) { await TransactionFiles.VerifyFile(destination, entry, token); return; }
        var temporary = PathPolicy.Resolve(OneDriveFiles.Relay(publication.Profile),
            "pending/" + publication.Snapshot.Id + "/" + entry.Hash);
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        await using (var source = FileScanner.OpenRead(PathPolicy.Resolve(publication.Profile.TargetPath, entry.Path)))
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                         FileShare.None, FileScanner.BufferSize, FileOptions.Asynchronous))
        {
            await source.CopyToAsync(output, token);
            await output.FlushAsync(token);
            output.Flush(true);
        }
        await TransactionFiles.VerifyFile(temporary, entry, token);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(temporary, destination);
    }

    private static void Commit(Profile profile, Snapshot snapshot)
    {
        var path = PathPolicy.Resolve(OneDriveFiles.Relay(profile), "snapshots/" + snapshot.Id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonFiles.Write(path, snapshot);
        var hash = SnapshotMetadata.Hash(JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonFiles.Options));
        JsonFiles.Write(PathPolicy.Resolve(OneDriveFiles.Relay(profile), "current.json"), new SnapshotHead(snapshot.Id, hash));
    }
}
