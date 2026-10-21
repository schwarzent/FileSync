using FileSync.Core;
using FileSync.Network;
using FileSync.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace FileSync.Tests;

public static class TwoWayFaultTests
{
    private sealed class LostCommitResponse(PeerClient client) : IRemoteFiles, IRemoteTransactions
    {
        public Task<Manifest> Manifest(Profile profile, CancellationToken token) => client.Manifest(profile, token);
        public Task Download(DownloadRequest request, CancellationToken token) => client.Download(request, token);
        public Task Prepare(Profile profile, SyncPlan plan, CancellationToken token) => client.Prepare(profile, plan, token);
        public Task Upload(UploadRequest request, CancellationToken token) => client.Upload(request, token);
        public Task<string> Status(Profile profile, string id, CancellationToken token) => client.Status(profile, id, token);
        public async Task Control(RemoteCommand command, CancellationToken token)
        {
            await client.Control(command, token);
            if (command.Action == "commit") throw new HttpRequestException("模拟远端提交后响应丢失");
        }
    }

    public static async Task Run(TwoWayContext context, CancellationToken token)
    {
        await LostResponse(context, token);
        await InterruptedLocalWrite(context, token);
        await Prepared(context, token);
        await InvalidUpload(context, token);
        await LargeUpload(context, token);
        await UndoDrift(context, token);
        PlannerConflicts();
    }

    private static async Task LostResponse(TwoWayContext context, CancellationToken token)
    {
        File.WriteAllText(Path.Combine(context.Target, "upload.txt"), "upload before response loss");
        File.WriteAllText(Path.Combine(context.Source, "download.txt"), "download before response loss");
        var scanner = new FileScanner();
        var engine = new SyncEngine(context.Local.Store, scanner, new LostCommitResponse(context.Local.Client), new TransactionFiles(context.Local.Store));
        var plan = await engine.Preview(context.Profile.Id, token);
        await Check.Reject(() => engine.Execute(plan.Id, token), "远端真实提交后响应丢失会显式失败");
        Check.True(context.Local.Store.History().First(r => r.Id == plan.Id).Status == "recovery-needed", "提交响应丢失保留两端恢复记录");
        await Check.Reject(() => engine.Preview(context.Profile.Id, token), "双向事务未恢复前拒绝新的同步");
        await engine.Recover(plan.Id, token);
        Check.True(FileScanner.Equal(plan.Local, await scanner.Scan(context.Target, token)) &&
            FileScanner.Equal(plan.Remote, await scanner.Scan(context.Source, token)), "响应丢失后恢复同时还原两端，保留原始独有文件");
        await TwoWayTests.Execute(context, token);
    }

    private static async Task InterruptedLocalWrite(TwoWayContext context, CancellationToken token)
    {
        File.WriteAllText(Path.Combine(context.Target, "upload.txt"), "local changed again");
        File.WriteAllText(Path.Combine(context.Source, "download.txt"), "remote changed again");
        var plan = await context.Local.Engine.Preview(context.Profile.Id, token);
        await context.Local.Client.Prepare(context.Profile, plan, token);
        var files = new TransactionFiles(context.Local.Store);
        foreach (var entry in plan.Changes.Where(c => c.After is { Directory: false } && c.Kind != "metadata").Select(c => c.After!))
            await context.Local.Client.Download(new(context.Profile, entry, files.Stage(plan, entry), _ => { }), token);
        foreach (var entry in plan.RemoteChanges.Where(c => c.After is { Directory: false } && c.Kind != "metadata").Select(c => c.After!))
            await context.Local.Client.Upload(new(context.Profile, plan.Id, entry), token);
        await context.Local.Client.Control(new(context.Profile, plan.Id, "commit"), token);
        ArchiveInterrupted(context, plan, true);
        files.Archive(plan);
        files.Install(plan);
        await context.Local.Engine.Recover(plan.Id, token);
        var scanner = new FileScanner();
        Check.True(FileScanner.Equal(plan.Local, await scanner.Scan(context.Target, token)) &&
            FileScanner.Equal(plan.Remote, await scanner.Scan(context.Source, token)), "两端文件安装后中断可完整恢复");
        await TwoWayTests.Execute(context, token);
    }

    private static async Task Prepared(TwoWayContext context, CancellationToken token)
    {
        var plan = await context.Local.Engine.Preview(context.Profile.Id, token);
        await context.Local.Client.Prepare(context.Profile, plan, token);
        ArchiveInterrupted(context, plan, false);
        var incoming = context.Remote.App.Services.GetRequiredService<IncomingTransactions>();
        await Check.Reject(() => incoming.Status(new(plan.Id, new string('F', 64))), "其他设备不能查询或操作已准备的事务");
        File.WriteAllText(Path.Combine(context.Target, "external-local.txt"), "edited while staging");
        File.WriteAllText(Path.Combine(context.Source, "external-remote.txt"), "edited while staging");
        var scanner = new FileScanner();
        var localEdited = await scanner.Scan(context.Target, token);
        var remoteEdited = await scanner.Scan(context.Source, token);
        await context.Local.Engine.Recover(plan.Id, token);
        Check.True(await context.Local.Client.Status(context.Profile, plan.Id, token) == "reverted", "仅准备阶段中断可恢复远端事务");
        Check.True(FileScanner.Equal(localEdited, await scanner.Scan(context.Target, token)) &&
            FileScanner.Equal(remoteEdited, await scanner.Scan(context.Source, token)), "取消尚未写入的事务保留两端外部编辑");
    }

    private static async Task InvalidUpload(TwoWayContext context, CancellationToken token)
    {
        File.WriteAllText(Path.Combine(context.Target, "invalid-upload.txt"), "expected complete content");
        var plan = await context.Local.Engine.Preview(context.Profile.Id, token);
        await context.Local.Client.Prepare(context.Profile, plan, token);
        ArchiveInterrupted(context, plan, false);
        using var client = context.Local.Client.Connect(context.Local.Client.Find(context.Profile.PeerId));
        using var content = new StringContent("bad");
        using var response = await client.PostAsync($"peer/transactions/{plan.Id}/file?path=invalid-upload.txt", content, token);
        Check.True((int)response.StatusCode == 409 && !File.Exists(Path.Combine(context.Source, "invalid-upload.txt")),
            "不完整上传明确报错且不写入远端目标");
        using var traversal = await client.PostAsync($"peer/transactions/{plan.Id}/file?path=..%2Foutside.txt", new StringContent("bad"), token);
        Check.True((int)traversal.StatusCode == 400, "上传路径只能来自已授权事务清单");
        await context.Local.Engine.Recover(plan.Id, token);
    }

    private static void ArchiveInterrupted(TwoWayContext context, SyncPlan plan, bool started)
    {
        Directory.CreateDirectory(context.Local.Store.VaultPath(plan.Id));
        JsonFiles.Write(Path.Combine(context.Local.Store.VaultPath(plan.Id), "plan.json"), plan);
        JsonFiles.Write(context.Local.Store.RevisionPath(plan.Id), new Revision(plan.Id, context.Profile.Id,
            context.Profile.Name, "writing", DateTime.UtcNow, plan.Changes) { LocalWriteStarted = started });
    }

    private static async Task UndoDrift(TwoWayContext context, CancellationToken token)
    {
        var plan = await TwoWayTests.Execute(context, token);
        File.WriteAllText(Path.Combine(context.Source, "upload.txt"), "remote unsaved work");
        await Check.Reject(() => context.Local.Engine.Undo(plan.Id, token), "远端同步后修改会阻止双向撤销");
        File.WriteAllText(Path.Combine(context.Source, "upload.txt"), File.ReadAllText(Path.Combine(context.Target, "upload.txt")));
        TransactionFiles.RestoreTimes(context.Source, plan.Desired);
        await context.Local.Engine.Undo(plan.Id, token);
    }

    private static async Task LargeUpload(TwoWayContext context, CancellationToken token)
    {
        const long payloadBytes = 32 * 1024 * 1024;
        var path = Path.Combine(context.Target, "large-upload.bin");
        using (var stream = new FileStream(path, FileMode.CreateNew)) stream.SetLength(payloadBytes);
        var plan = await TwoWayTests.Execute(context, token);
        Check.True(new FileInfo(Path.Combine(context.Source, "large-upload.bin")).Length == payloadBytes,
            "32 MiB 文件通过流式上传，超过服务器默认请求体大小仍能完成校验");
        await context.Local.Engine.Undo(plan.Id, token);
        File.Move(path, Path.Combine(Path.GetDirectoryName(context.Target)!, "retained-large-upload.bin"));
    }

    private static void PlannerConflicts()
    {
        const long ticks = 100;
        var directory = new Entry("folder", true, 0, ticks, null);
        var file = new Entry("folder/file.txt", false, 1, ticks, new string('A', 64));
        var baseline = new Manifest(ticks, [directory, file]);
        var changed = baseline with { Entries = [directory, file with { Hash = new string('B', 64) }] };
        var deleted = new Manifest(ticks, []);
        Check.True(TwoWayPlanner.Merge(changed, deleted, baseline).Conflicts.Count > 0, "目录删除与另一端子文件修改产生冲突");
        var newChild = baseline with { Entries = [directory, file, file with { Path = "folder/new.txt" }] };
        Check.True(TwoWayPlanner.Merge(newChild, deleted, baseline).Conflicts.Count > 0, "目录删除与另一端新增子文件产生冲突");
        var replaced = new Manifest(ticks, [file with { Path = "folder" }]);
        Check.True(TwoWayPlanner.Merge(newChild, replaced, baseline).Conflicts.Count > 0, "目录替换为文件与另一端子项修改产生冲突");
    }
}
