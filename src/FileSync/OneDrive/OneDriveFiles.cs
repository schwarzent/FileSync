using System.Text.Json;
using FileSync.Core;
using FileSync.Network;
using FileSync.Sync;

namespace FileSync.OneDrive;

public sealed class OneDriveFiles : IRemoteFiles
{
    public static string Relay(Profile profile) => PathPolicy.Root(profile.RelayPath ?? "");
    public static string Metadata(Profile profile, string relative) =>
        PathPolicy.Resolve(Relay(profile), RepositoryRules.MetadataDirectory + "/" + relative);

    public static string Blob(Profile profile, string hash)
    {
        if (!SnapshotMetadata.ValidHash(hash)) throw new UserError("OneDrive 内容哈希无效。");
        return Metadata(profile, "objects/" + hash);
    }

    public static SnapshotHead? Head(Profile profile)
    {
        var path = Metadata(profile, "current.json");
        if (!File.Exists(path))
        {
            var legacy = PathPolicy.Resolve(Relay(profile), "current.json");
            if (File.Exists(legacy) && Directory.Exists(PathPolicy.Resolve(Relay(profile), "snapshots")))
                throw new UserError("此路径是旧版中继。请为普通文件仓库选择另一个文件夹，旧中继内容保持保留。");
            return null;
        }
        var head = JsonFiles.Read<SnapshotHead>(path);
        StateStore.SafeId(head.SnapshotId);
        if (!SnapshotMetadata.ValidHash(head.Hash)) throw new UserError("OneDrive 发布记录哈希无效。");
        return head;
    }

    public static Snapshot ReadSnapshot(Profile profile)
    {
        RepositoryWorkspace.AssertReady(profile);
        var head = Head(profile) ?? throw new UserError("尚未收到 OneDrive 发布记录，请先在来源端发布并等待 OneDrive 同步。", 409);
        return ReadCommit(profile, head);
    }

    public static Snapshot ReadCommit(Profile profile, SnapshotHead head)
    {
        StateStore.SafeId(head.SnapshotId);
        if (!SnapshotMetadata.ValidHash(head.Hash)) throw new UserError("仓库提交哈希无效。");
        var path = Metadata(profile, "snapshots/" + head.SnapshotId + ".json");
        if (!File.Exists(path)) throw new UserError("OneDrive 清单尚未下载完整，请等待 OneDrive 同步后重新预览。", 409);
        var bytes = File.ReadAllBytes(path);
        if (SnapshotMetadata.Hash(bytes) != head.Hash) throw new UserError("OneDrive 清单哈希不匹配，请检查同步状态。", 409);
        var snapshot = JsonSerializer.Deserialize<Snapshot>(bytes, JsonFiles.Options)
            ?? throw new UserError("OneDrive 清单为空。");
        if (snapshot.Id != head.SnapshotId) throw new UserError("OneDrive 发布记录与清单编号不一致。", 409);
        SnapshotMetadata.Validate(snapshot, profile.TargetPath);
        return snapshot;
    }

    public async Task<Manifest> Manifest(Profile profile, CancellationToken token)
    {
        var snapshot = ReadSnapshot(profile);
        var manifest = SyncScope.Filter(snapshot.Manifest, RepositoryRules.Pull(profile));
        await VerifyObjects(profile, manifest, token);
        foreach (var entry in manifest.Entries.Where(e => e.Directory))
            if (!Directory.Exists(PathPolicy.Resolve(Relay(profile), entry.Path)))
                throw new UserError($"OneDrive 目录尚未同步完整：{entry.Path}", 409);
        foreach (var entry in manifest.Entries.Where(e => !e.Directory))
            await TransactionFiles.VerifyFile(PathPolicy.Resolve(Relay(profile), entry.Path), entry, token);
        return manifest;
    }

    public static async Task VerifyObjects(Profile profile, Manifest manifest, CancellationToken token)
    {
        foreach (var entry in manifest.Entries.Where(e => !e.Directory).DistinctBy(e => e.Hash))
        {
            token.ThrowIfCancellationRequested();
            var path = Blob(profile, entry.Hash!);
            if (!File.Exists(path)) throw new UserError($"OneDrive 内容尚未下载完整：{entry.Path}。请等待同步后重新预览。", 409);
            await TransactionFiles.VerifyFile(path, entry, token);
        }
    }

    public async Task Download(DownloadRequest request, CancellationToken token)
    {
        await using var source = FileScanner.OpenRead(PathPolicy.Resolve(Relay(request.Profile), request.Entry.Path));
        await using var destination = new FileStream(request.Destination, FileMode.CreateNew,
            FileAccess.Write, FileShare.None, FileScanner.BufferSize, FileOptions.Asynchronous);
        var buffer = new byte[FileScanner.BufferSize];
        long received = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            received += count;
            if (received > request.Entry.Length) throw new UserError("OneDrive 内容超过清单声明的大小。", 409);
            await destination.WriteAsync(buffer.AsMemory(0, count), token);
            request.Report(count);
        }
        await destination.FlushAsync(token);
        destination.Flush(true);
        if (received != request.Entry.Length) throw new UserError("OneDrive 内容下载不完整。", 409);
    }
}
