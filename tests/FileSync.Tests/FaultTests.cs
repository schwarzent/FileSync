using FileSync.Core;
using FileSync.Network;
using FileSync.Sync;

namespace FileSync.Tests;

public static class FaultTests
{
    private sealed class FaultRemote(Manifest manifest) : IRemoteFiles
    {
        public bool Corrupt { get; init; }
        public bool Wait { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Manifest> Manifest(Profile profile, CancellationToken token) => Task.FromResult(manifest);
        public async Task Download(DownloadRequest request, CancellationToken token)
        {
            Started.TrySetResult();
            if (Wait) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            await File.WriteAllTextAsync(request.Destination, Corrupt ? "错误内容" : "测试内容", token);
        }
    }

    public static async Task Run(string root, CancellationToken token)
    {
        var source = Path.Combine(root, "异常测试源");
        var target = Path.Combine(root, "异常测试目标");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(source, "文件.txt"), "测试内容", token);
        var scanner = new FileScanner();
        var remoteManifest = await scanner.Scan(source, token);
        var original = await scanner.Scan(target, token);
        var store = new StateStore(Path.Combine(root, "异常测试状态"));
        var peer = new Peer(new string('A', 64), "测试设备", "127.0.0.1", 12345);
        var profile = new Profile(StateStore.NewId(), "异常验证", peer.Id, StateStore.NewId(), target, null);
        store.Update(s => s with { Peers = [peer], Profiles = [profile] });
        var corrupt = new FaultRemote(remoteManifest) { Corrupt = true };
        var engine = new SyncEngine(store, scanner, corrupt, new TransactionFiles(store));
        var plan = await engine.Preview(profile.Id, token);
        await Check.Reject(() => engine.Execute(plan.Id, token), "传输内容损坏会导致完整性校验失败");
        Check.True(FileScanner.Equal(original, await scanner.Scan(target, token)), "完整性校验失败不修改目标");
        var waiting = new FaultRemote(remoteManifest) { Wait = true };
        var cancelEngine = new SyncEngine(store, scanner, waiting, new TransactionFiles(store));
        var next = await cancelEngine.Preview(profile.Id, token);
        var running = cancelEngine.Execute(next.Id, token);
        await waiting.Started.Task.WaitAsync(token);
        await Check.Reject(() => cancelEngine.Preview(profile.Id, token), "同步期间禁止并行修改任务");
        cancelEngine.Cancel();
        await Check.Reject(() => running, "传输可由用户取消");
        Check.True(FileScanner.Equal(original, await scanner.Scan(target, token)), "取消操作保留原始文件夹");
        Check.True(!cancelEngine.Busy && cancelEngine.Progress.Phase == "idle", "取消后释放互斥锁与进度资源");
        MalformedManifests(remoteManifest, target);
    }

    private static void MalformedManifests(Manifest manifest, string target)
    {
        var entry = manifest.Entries.Single();
        Check.Reject(() => FileScanner.Validate(manifest with { Entries = [entry, entry] }, target), "拒绝远端重名文件清单");
        Check.Reject(() => FileScanner.Validate(manifest with { Entries = [entry with { Path = "../outside" }] }, target), "拒绝远端穿越路径");
        Check.Reject(() => FileScanner.Validate(manifest with { Entries = [entry with { Path = "missing/child" }] }, target), "拒绝缺少父目录的清单");
        Check.Reject(() => FileScanner.Validate(manifest with { Entries = [entry with { Hash = "invalid" }] }, target), "拒绝无效文件哈希");
        Check.Reject(() => FileScanner.Validate(null, target), "拒绝空清单");
        Check.Reject(() => FileScanner.Validate(manifest with { Entries = [null!] }, target), "拒绝清单空条目");
        Check.Reject(() => FileScanner.Validate(manifest with { Entries = [entry with { Directory = true }] }, target), "拒绝带文件大小或哈希的目录条目");
    }
}
