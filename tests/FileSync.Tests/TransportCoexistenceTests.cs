using System.Net.Http.Json;
using FileSync.Core;

namespace FileSync.Tests;

public sealed record TransportCoexistenceContext(TestNode Local, TestNode Remote, Profile Lan)
{
    public required Profile Cloud { get; init; }
    public required Profile Publisher { get; init; }
    public required string Source { get; init; }
}

public static class TransportCoexistenceTests
{
    private static readonly DateTime CloudModified = new(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    public static async Task Run(SyncResourceContext nodes, CancellationToken token)
    {
        var localBefore = nodes.Local.Store.Current;
        var remoteBefore = nodes.Remote.Store.Current;
        try
        {
            var context = await Initialize(nodes, token);
            Paths(context);
            await Configuration(context, token);
            await Transfer(context, token);
        }
        finally
        {
            nodes.Local.Store.Update(_ => localBefore);
            nodes.Remote.Store.Update(_ => remoteBefore);
        }
    }

    private static async Task<TransportCoexistenceContext> Initialize(SyncResourceContext nodes, CancellationToken token)
    {
        var root = Path.Combine(nodes.Root, "共用资料目录 [中文]");
        var source = Path.Combine(root, "局域网来源");
        var target = Path.Combine(root, "本机资料");
        var publisher = Path.Combine(root, "云端来源");
        foreach (var path in new[] { source, target, publisher }) Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(source, "远端资料.txt"), "局域网资料");
        File.WriteAllText(Path.Combine(target, "本机资料.txt"), "本机原有内容");
        var share = new Share(StateStore.NewId(), "混合传输来源", source) { AllowWrite = true };
        nodes.Remote.Store.Update(s => s with { Shares = s.Shares.Append(share).ToArray() });
        var cloud = await Save(nodes.Local, new Profile(StateStore.NewId(), "共用目录 OneDrive", "", "", target, null)
        {
            Transport = SyncTransports.OneDrive, Mode = SyncModes.Latest, RelayPath = Path.Combine(root, "本机中继")
        }, token);
        var lan = await Save(nodes.Local, new Profile(StateStore.NewId(), "共用目录局域网", nodes.Remote.Identity.Id,
            share.Id, target, Path.Combine(root, "局域网备份")) { Mode = SyncModes.TwoWay }, token);
        var sender = await Save(nodes.Remote, cloud with { Id = StateStore.NewId(), Name = "云端发布",
            TargetPath = publisher, RelayPath = Path.Combine(root, "远端中继") }, token);
        using var response = await nodes.Local.Ui.PostAsJsonAsync("api/shares",
            new { name = "共用资料共享", path = target, allowWrite = true }, token);
        Check.True(response.IsSuccessStatusCode, "同一资料目录也可发布为本机共享");
        return new(nodes.Local, nodes.Remote, lan) { Cloud = cloud, Publisher = sender, Source = source };
    }

    private static async Task<Profile> Save(TestNode node, Profile profile, CancellationToken token)
    {
        using var response = await node.Ui.PostAsJsonAsync("api/profiles", profile, token);
        Check.True(response.IsSuccessStatusCode, "管理接口允许局域网与 OneDrive 任务共用目标目录");
        return node.Store.Current.Profiles.Single(p => p.Id == profile.Id);
    }

    private static void Paths(TransportCoexistenceContext context)
    {
        var store = context.Local.Store;
        var settings = store.Current;
        var variant = context.Lan with { Id = StateStore.NewId(), Mode = SyncModes.OneWay,
            TargetPath = context.Lan.TargetPath.ToUpperInvariant() + Path.DirectorySeparatorChar };
        store.Validate(settings with { Profiles = settings.Profiles.Append(variant).ToArray() });
        Check.True(true, "同目录的单向、双向及 OneDrive 任务兼容大小写和末尾分隔符");
        var invalid = new[]
        {
            context.Lan with { TargetPath = Path.Combine(context.Lan.TargetPath, "子目录") },
            context.Lan with { TargetPath = Path.GetDirectoryName(context.Lan.TargetPath)! },
            context.Lan with { TargetPath = store.Root },
            context.Lan with { TargetPath = Path.GetDirectoryName(store.Root)! },
            context.Lan with { BackupPath = context.Lan.TargetPath },
            context.Cloud with { RelayPath = context.Cloud.TargetPath },
            context.Cloud with { RelayPath = Path.Combine(context.Cloud.TargetPath, "中继") }
        };
        foreach (var profile in invalid)
            Check.Reject(() => store.Validate(settings with
            {
                Profiles = settings.Profiles.Select(p => p.Id == profile.Id ? profile : p).ToArray()
            }), "共用目录仍拒绝嵌套目录及资料、备份、中继、程序数据冲突");
    }

    private static async Task Configuration(TransportCoexistenceContext context, CancellationToken token)
    {
        var node = context.Local;
        var reloaded = new StateStore(node.Store.Root);
        reloaded.Validate(reloaded.Current);
        Check.True(reloaded.Current.Profiles.Contains(context.Lan) && reloaded.Current.Profiles.Contains(context.Cloud),
            "共用目标目录的两种任务可持久化并通过启动校验");
        var backup = await node.Ui.GetFromJsonAsync<TaskConfigurationBackup>(TaskBackupTests.Endpoint + "export", token)
            ?? throw new InvalidOperationException("配置备份为空");
        node.Store.Update(s => s with { Profiles = s.Profiles.Where(p => p.Id != context.Lan.Id && p.Id != context.Cloud.Id).ToArray() });
        var preview = await TaskBackupTests.Preview(node, backup, token);
        var imported = await TaskBackupTests.Import(node, new(backup, preview.ConfigurationHash), token);
        Check.True(imported.Added == 2 && node.Store.Current.Profiles.Contains(context.Lan) &&
            node.Store.Current.Profiles.Contains(context.Cloud), "配置备份同时恢复共用目录的局域网与 OneDrive 任务");
    }

    private static async Task Transfer(TransportCoexistenceContext context, CancellationToken token)
    {
        var initial = await context.Local.Engine.Preview(context.Lan.Id, token);
        await context.Local.Engine.Execute(initial.Id, token);
        Check.True(File.ReadAllText(Path.Combine(context.Source, "本机资料.txt")) == "本机原有内容" &&
            File.ReadAllText(Path.Combine(context.Lan.TargetPath, "远端资料.txt")) == "局域网资料",
            "共用目录任务通过真实 TLS 完成双向合并和完整备份");
        await OneDriveTests.Publish(context.Local, context.Cloud, token);
        Check.True(File.ReadAllText(Path.Combine(context.Cloud.RelayPath!, "远端资料.txt")) == "局域网资料",
            "局域网同步后的同一目录可立即发布至 OneDrive 仓库");
        OneDriveTests.CopyRelay(context.Cloud, context.Publisher);
        await OneDriveTests.Receive(context.Remote, context.Publisher, token);
        var stale = await context.Local.Engine.Preview(context.Lan.Id, token);
        await ReceiveCloudChange(context, token);
        await Check.Reject(() => context.Local.Engine.Execute(stale.Id, token), "OneDrive 接收改变共用目录后拒绝执行旧局域网预览");
        await context.Local.Engine.Recover(stale.Id, token);
        await Check.Reject(() => context.Local.Engine.Undo(initial.Id, token), "撤销局域网历史不会覆盖后续 OneDrive 接收的资料");
        var next = await context.Local.Engine.Preview(context.Lan.Id, token);
        Check.True(next.Conflicts.Count == 0, "交替执行两种传输时局域网任务继续使用自己的双向基线");
        await context.Local.Engine.Execute(next.Id, token);
        Check.True(File.ReadAllText(Path.Combine(context.Source, "云端新增.txt")) == "通过云端接收，再经局域网发送",
            "OneDrive 接收的新增文件可继续经真实 TLS 同步到局域网远端");
        await context.Local.Engine.Undo(next.Id, token);
        Check.True(File.Exists(Path.Combine(context.Lan.TargetPath, "云端新增.txt")) &&
            !File.Exists(Path.Combine(context.Source, "云端新增.txt")), "共用目录的最近局域网同步仍可独立撤销");
    }

    private static async Task ReceiveCloudChange(TransportCoexistenceContext context, CancellationToken token)
    {
        var path = Path.Combine(context.Publisher.TargetPath, "云端新增.txt");
        File.WriteAllText(path, "通过云端接收，再经局域网发送");
        File.SetLastWriteTimeUtc(path, CloudModified);
        await OneDriveTests.Publish(context.Remote, context.Publisher, token);
        OneDriveTests.CopyRelay(context.Publisher, context.Cloud);
        await OneDriveTests.Receive(context.Local, context.Cloud, token);
        Check.True(File.ReadAllText(Path.Combine(context.Lan.TargetPath, "云端新增.txt")) ==
            "通过云端接收，再经局域网发送", "共用目录可实际接收 OneDrive 修改");
        var history = context.Local.Store.History();
        Check.True(history.Any(r => r.ProfileId == context.Lan.Id && r.Status == "completed") &&
            history.Any(r => r.ProfileId == context.Cloud.Id && r.Status == "completed"), "共用目录的不同任务独立保留版本记录");
    }
}
