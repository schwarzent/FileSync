using System.Net.Http.Json;
using FileSync.Core;

namespace FileSync.Tests;

public sealed record TwoWayContext(TestNode Local, TestNode Remote, Profile Profile)
{
    public string Source { get; init; } = "";
    public string Target => Profile.TargetPath;
}

public static class TwoWayTests
{
    public static async Task Run(SyncResourceContext nodes, CancellationToken token)
    {
        var context = await Initialize(nodes, token);
        await Initial(context, token);
        await Incremental(context, token);
        await Conflicts(context, token);
        await Permissions(context, token);
        await TwoWayFaultTests.Run(context, token);
    }

    private static async Task<TwoWayContext> Initialize(SyncResourceContext nodes, CancellationToken token)
    {
        var source = Path.Combine(nodes.Root, "two-way-remote");
        var target = Path.Combine(nodes.Root, "two-way-local");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "remote.txt"), "remote only");
        File.WriteAllText(Path.Combine(target, "local.txt"), "local only");
        File.WriteAllText(Path.Combine(source, "shared.txt"), "shared");
        File.WriteAllText(Path.Combine(target, "shared.txt"), "shared");
        using var shareResponse = await nodes.Remote.Ui.PostAsJsonAsync("api/shares",
            new { name = "双向验证", path = source, allowWrite = true }, token);
        Check.True(shareResponse.IsSuccessStatusCode, "管理接口允许显式启用共享写入");
        var share = nodes.Remote.Store.Current.Shares.Single(s => s.Path == source);
        using var profileResponse = await nodes.Local.Ui.PostAsJsonAsync("api/profiles", new
        {
            name = "双向验证", peerId = nodes.Remote.Identity.Id, shareId = share.Id,
            targetPath = target, backupPath = Path.Combine(nodes.Root, "two-way-backup"), mode = SyncModes.TwoWay
        }, token);
        Check.True(profileResponse.IsSuccessStatusCode, "管理接口保存双向同步选项");
        var profile = nodes.Local.Store.Current.Profiles.Single(p => p.TargetPath == target);
        Check.True(new StateStore(nodes.Local.Store.Root).Current.Profiles.Single(p => p.Id == profile.Id).Mode == SyncModes.TwoWay,
            "双向选项持久化，旧任务继续默认单向");
        return new(nodes.Local, nodes.Remote, profile) { Source = source };
    }

    private static async Task Initial(TwoWayContext context, CancellationToken token)
    {
        var scanner = new FileScanner();
        var localBefore = await scanner.Scan(context.Target, token);
        var remoteBefore = await scanner.Scan(context.Source, token);
        var plan = await context.Local.Engine.Preview(context.Profile.Id, token);
        Check.True(plan.Conflicts.Count == 0 && plan.Changes.Count > 0 && plan.RemoteChanges.Count > 0,
            "首次双向预览分别显示两端变化，同内容的不同时间不冲突");
        var revision = await context.Local.Engine.Execute(plan.Id, token);
        Check.True(revision.Mode == SyncModes.TwoWay && revision.RemoteChanges.SequenceEqual(plan.RemoteChanges),
            "发起端版本详情保留同步方向及远端变更");
        await AssertEqual(context, plan.Desired, token);
        Check.True(File.Exists(Path.Combine(context.Target, "remote.txt")) && File.Exists(Path.Combine(context.Source, "local.txt")),
            "真实 TLS 双向传输合并两边独有文件");
        Check.True(FileScanner.Equal(await scanner.Scan(revision.BackupPath!, token), localBefore), "双向同步前完整备份保留本机原始内容");
        await context.Local.Engine.Undo(plan.Id, token);
        Check.True(FileScanner.Equal(localBefore, await scanner.Scan(context.Target, token)) &&
            FileScanner.Equal(remoteBefore, await scanner.Scan(context.Source, token)), "双向撤销同时恢复两端内容与目录时间");
        Check.True(!context.Local.Engine.Busy && context.Local.Engine.Progress.Phase == "idle", "双向撤销后释放进度状态");
        var next = await context.Local.Engine.Preview(context.Profile.Id, token);
        await context.Local.Engine.Execute(next.Id, token);
    }

    private static async Task Incremental(TwoWayContext context, CancellationToken token)
    {
        File.WriteAllText(Path.Combine(context.Target, "local.txt"), "local updated");
        File.WriteAllText(Path.Combine(context.Source, "remote.txt"), "remote updated");
        Directory.CreateDirectory(Path.Combine(context.Target, "empty"));
        await Execute(context, token);
        Check.True(File.ReadAllText(Path.Combine(context.Source, "local.txt")) == "local updated" &&
            File.ReadAllText(Path.Combine(context.Target, "remote.txt")) == "remote updated", "后续同步同时传播两端修改和空目录");
        File.Move(Path.Combine(context.Target, "local.txt"), Path.Combine(Path.GetDirectoryName(context.Target)!, "deleted-local.txt"));
        File.Move(Path.Combine(context.Source, "remote.txt"), Path.Combine(Path.GetDirectoryName(context.Source)!, "deleted-remote.txt"));
        await Execute(context, token);
        Check.True(!File.Exists(Path.Combine(context.Source, "local.txt")) && !File.Exists(Path.Combine(context.Target, "remote.txt")),
            "三方比较将两端删除传播到另一端，不重新生成已删除文件");
    }

    private static async Task Conflicts(TwoWayContext context, CancellationToken token)
    {
        File.WriteAllText(Path.Combine(context.Target, "shared.txt"), "local conflict");
        File.WriteAllText(Path.Combine(context.Source, "shared.txt"), "remote conflict");
        var scanner = new FileScanner();
        var before = await scanner.Scan(context.Target, token);
        var remoteBefore = await scanner.Scan(context.Source, token);
        var plan = await context.Local.Engine.Preview(context.Profile.Id, token);
        Check.True(plan.Conflicts.Contains("shared.txt"), "同一文件两端改动时明确列出冲突路径");
        await Check.Reject(() => context.Local.Engine.Execute(plan.Id, token), "冲突预览禁止执行覆盖");
        Check.True(FileScanner.Equal(before, await scanner.Scan(context.Target, token)) &&
            FileScanner.Equal(remoteBefore, await scanner.Scan(context.Source, token)), "冲突不修改两端文件");
        File.WriteAllText(Path.Combine(context.Source, "shared.txt"), "local conflict");
        await Execute(context, token);
    }

    private static async Task Permissions(TwoWayContext context, CancellationToken token)
    {
        var share = context.Remote.Store.Current.Shares.Single(s => s.Id == context.Profile.ShareId);
        using var response = await context.Remote.Ui.PostAsJsonAsync("api/shares",
            new { share.Id, share.Name, share.Path, allowWrite = false }, token);
        Check.True(response.IsSuccessStatusCode, "已有共享可在管理界面关闭双向写入");
        var plan = await context.Local.Engine.Preview(context.Profile.Id, token);
        await Check.Reject(() => context.Local.Engine.Execute(plan.Id, token), "只读共享拒绝远端写入事务");
        await context.Local.Engine.Recover(plan.Id, token);
        context.Remote.Store.Update(s => s with { Shares = s.Shares.Select(x => x.Id == share.Id ? x with { AllowWrite = true } : x).ToArray() });
        var pending = await context.Local.Engine.Preview(context.Profile.Id, token);
        await context.Local.Client.Prepare(context.Profile, pending, token);
        context.Remote.Store.Update(s => s with { Shares = s.Shares.Select(x => x.Id == share.Id ? x with { AllowWrite = false } : x).ToArray() });
        await Check.Reject(() => context.Local.Client.Control(new(context.Profile, pending.Id, "commit"), token),
            "准备事务后撤销写入授权会阻止提交");
        await context.Local.Client.Control(new(context.Profile, pending.Id, "recover"), token);
        context.Remote.Store.Update(s => s with { Shares = s.Shares.Select(x => x.Id == share.Id ? x with { AllowWrite = true } : x).ToArray() });
    }

    public static async Task<SyncPlan> Execute(TwoWayContext context, CancellationToken token)
    {
        var plan = await context.Local.Engine.Preview(context.Profile.Id, token);
        Check.True(plan.Conflicts.Count == 0, "无冲突的双向变更可以执行");
        await context.Local.Engine.Execute(plan.Id, token);
        await AssertEqual(context, plan.Desired, token);
        return plan;
    }

    public static async Task AssertEqual(TwoWayContext context, Manifest expected, CancellationToken token)
    {
        var scanner = new FileScanner();
        Check.True(FileScanner.Equal(expected, await scanner.Scan(context.Target, token)) &&
            FileScanner.Equal(expected, await scanner.Scan(context.Source, token)), "双向同步后两端内容、文件及目录时间一致");
    }
}
