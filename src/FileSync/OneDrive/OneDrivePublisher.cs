using System.Text.Json;
using FileSync.Core;
using FileSync.Sync;

namespace FileSync.OneDrive;

public sealed record PublishResult(string SnapshotId, int Files, int Directories)
{
    public long Sequence { get; init; }
    public int ChangedFiles { get; init; }
    public string Message => "已写入本机 OneDrive 中继，等待 OneDrive 客户端上传；另一端下载校验后才能确认同步完成。";
}

public sealed class OneDrivePublisher(StateStore store, FileScanner scanner)
{
    public async Task<PublishResult> Publish(Profile profile, CancellationToken token, Action<SyncProgress> report)
    {
        RepositoryWorkspace.AssertReady(profile);
        var head = OneDriveFiles.Head(profile);
        var previous = head is null ? null : OneDriveFiles.ReadCommit(profile, head);
        var before = await RepositoryWorkspace.Scan(profile, scanner, token);
        if (previous is not null)
        {
            await OneDriveFiles.VerifyObjects(profile, previous.Manifest, token);
            RepositoryWorkspace.AssertContent(before, previous.Manifest);
        }
        var patterns = RepositoryRules.Upload(profile);
        var manifest = await scanner.ScanMetadata(profile.TargetPath, token, patterns);
        RepositoryRules.ValidatePaths(manifest);
        var repository = previous?.Manifest ?? (before.Entries.Count == 0 ? RepositoryRules.Normalize(manifest with { Entries = [] }) : before);
        var desired = Merge(profile, manifest, repository);
        var baseline = previous?.Manifest ?? repository with { Entries = [] };
        var snapshot = new Snapshot(StateStore.NewId(), desired, SnapshotMetadata.DirectoryHashes(desired))
            { Publisher = store.Current.Name, PublishedUtc = DateTime.UtcNow, Parent = head,
                Sequence = (previous?.Sequence ?? 0) + 1, Changes = Planner.Compare(baseline, desired) };
        SnapshotMetadata.Validate(snapshot, profile.TargetPath);
        await PublishContent(new(profile, snapshot, report)
            { LocalHashes = manifest.Entries.Where(e => !e.Directory).ToDictionary(e => e.Path, e => e.Hash, StringComparer.Ordinal) }, token);
        if (!FileScanner.Equal(manifest, await scanner.ScanMetadata(profile.TargetPath, token, patterns)))
            throw new UserError("发布期间本机文件或时间已改变，请重新发布。", 409);
        RepositoryWorkspace.AssertContent(await RepositoryWorkspace.Scan(profile, scanner, token), before);
        token.ThrowIfCancellationRequested();
        if (OneDriveFiles.Head(profile) != head) throw new UserError("发布期间 OneDrive 收到了其他发布记录，请重新检查后发布。", 409);
        report(new("writing", null, 0, 0));
        await Commit(profile, snapshot, before);
        return new(snapshot.Id, desired.Entries.Count(e => !e.Directory), desired.Entries.Count(e => e.Directory))
            { Sequence = snapshot.Sequence, ChangedFiles = snapshot.Changes.Count(c => !(c.After ?? c.Before)!.Directory) };
    }

    private Manifest Merge(Profile profile, Manifest local, Manifest remote)
    {
        var plan = LatestPlanner.Merge(new(StateStore.NewId(), profile, local,
            SyncScope.Filter(remote, local.IgnorePatterns), [], DateTime.UtcNow));
        var selected = PublicationMerge.Resolve(store, plan).Desired;
        var rules = new IgnoreRules(local.IgnorePatterns);
        var preserved = remote.Entries.Where(rules.ExcludesPath);
        var desired = RepositoryRules.Normalize(selected with { Entries = selected.Entries.Concat(preserved)
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray() });
        FileScanner.Validate(desired, profile.TargetPath);
        return desired;
    }

    private sealed record Publication(Profile Profile, Snapshot Snapshot, Action<SyncProgress> Report)
    {
        public required IReadOnlyDictionary<string, string?> LocalHashes { get; init; }
    }

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
        var temporary = OneDriveFiles.Metadata(publication.Profile, "pending/" + publication.Snapshot.Id + "/" + entry.Hash);
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        var sourceRoot = publication.LocalHashes.TryGetValue(entry.Path, out var hash) && hash == entry.Hash
            ? publication.Profile.TargetPath : OneDriveFiles.Relay(publication.Profile);
        await using (var source = FileScanner.OpenRead(PathPolicy.Resolve(sourceRoot, entry.Path)))
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

    private async Task Commit(Profile profile, Snapshot snapshot, Manifest before)
    {
        var path = OneDriveFiles.Metadata(profile, "snapshots/" + snapshot.Id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonFiles.Write(path, snapshot);
        var hash = SnapshotMetadata.Hash(JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonFiles.Options));
        var update = new PublicationState(snapshot.Parent, new(snapshot.Id, hash), before);
        JsonFiles.Write(OneDriveFiles.Metadata(profile, RepositoryWorkspace.PendingFile), update);
        await RepositoryWorkspace.Finish(new(profile, update, scanner), CancellationToken.None);
    }
}
