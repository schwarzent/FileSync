using System.Net.Http.Json;
using FileSync.Core;

namespace FileSync.Tests;

public static class IgnoreTests
{
    public static async Task Run(SyncResourceContext nodes, CancellationToken token)
    {
        Rules();
        var context = await Configure(nodes, token);
        await OneWay(context, token);
        await TwoWay(context, token);
        await RuleChanges(context, token);
    }

    private static void Rules()
    {
        var rules = new IgnoreRules("# 注释\n.git/\n/build/\n**/cache/\n*.tmp\nlogs/?.txt");
        Check.True(rules.Excludes("a/.git", true) && !rules.Excludes(".git", false), "目录规则匹配任意层级，尾斜杠区分文件");
        Check.True(rules.Excludes("build", true) && !rules.Excludes("a/build", true), "前导斜杠限定同步根目录");
        Check.True(rules.Excludes("cache", true) && rules.Excludes("a/b/cache", true), "双星支持零层和多层目录");
        Check.True(rules.Excludes("a/工作.TMP", false) && rules.Excludes("logs/a.txt", false) &&
            !rules.Excludes("logs/ab.txt", false), "通配符支持中文及 Windows 大小写规则");
        foreach (var invalid in new[] { "!keep", "../outside", "C:/outside", "a\\b", "[abc]" })
            Check.Reject(() => _ = new IgnoreRules(invalid), "无效或不支持的规则明确报错：" + invalid);
        const long ticks = 100;
        var directory = new Entry("folder", true, 0, ticks, null);
        var before = new Manifest(ticks, [directory]) { ExcludedPaths = ["folder/cache"] };
        var after = new Manifest(ticks, [directory with { Directory = false, Length = 1, Hash = new string('A', 64) }]);
        Check.Reject(() => SyncScope.Preserve(after, before), "含忽略内容的父目录不能被替换为文件");
    }

    private static async Task<TwoWayContext> Configure(SyncResourceContext nodes, CancellationToken token)
    {
        var source = Path.Combine(nodes.Root, "ignore-source");
        var target = Path.Combine(nodes.Root, "ignore-target");
        foreach (var root in new[] { source, target })
        {
            Directory.CreateDirectory(Path.Combine(root, "private"));
            Directory.CreateDirectory(Path.Combine(root, "cache"));
            File.WriteAllText(Path.Combine(root, "private", "locked.txt"), root);
            File.WriteAllText(Path.Combine(root, "cache", "ignored.txt"), root);
            File.WriteAllText(Path.Combine(root, "scratch.tmp"), root);
            File.WriteAllText(Path.Combine(root, "visible.txt"), root);
        }
        Directory.CreateDirectory(Path.Combine(target, "gone", "cache"));
        File.WriteAllText(Path.Combine(target, "gone", "cache", "keep.txt"), "keep ignored child");
        File.WriteAllText(Path.Combine(target, "gone", "remove.txt"), "remove visible child");
        using var shared = await nodes.Remote.Ui.PostAsJsonAsync("api/shares", new
            { name = "忽略验证", path = source, allowWrite = true, ignorePatterns = "private/" }, token);
        Check.True(shared.IsSuccessStatusCode, "共享忽略规则通过管理接口保存");
        var share = nodes.Remote.Store.Current.Shares.Single(s => s.Path == source);
        using var saved = await nodes.Local.Ui.PostAsJsonAsync("api/profiles", new
        {
            name = "忽略验证", peerId = nodes.Remote.Identity.Id, shareId = share.Id, targetPath = target,
            backupPath = Path.Combine(nodes.Root, "ignore-backup"), ignorePatterns = "cache/\n*.tmp"
        }, token);
        Check.True(saved.IsSuccessStatusCode, "任务忽略规则通过管理接口保存");
        var profile = nodes.Local.Store.Current.Profiles.Single(p => p.TargetPath == target);
        Check.True(new StateStore(nodes.Local.Store.Root).Current.Profiles.Single(p => p.Id == profile.Id).IgnorePatterns == profile.IgnorePatterns,
            "忽略规则可持久化重新读取");
        return new(nodes.Local, nodes.Remote, profile) { Source = source };
    }

    private static async Task OneWay(TwoWayContext context, CancellationToken token)
    {
        using var localLock = new FileStream(Path.Combine(context.Target, "private", "locked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var remoteLock = new FileStream(Path.Combine(context.Source, "private", "locked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var plan = await context.Local.Engine.Preview(context.Profile.Id, token);
        Check.True(!plan.Local.Entries.Concat(plan.Remote.Entries).Any(e => e.Path.Contains("cache") || e.Path.Contains("private") || e.Path.EndsWith(".tmp")),
            "本机及远端在扫描时跳过忽略目录，包括被独占锁定的子文件");
        Check.True(!plan.Changes.Any(c => c.Path == "gone" && c.Kind == "delete"), "保留含忽略子项的父目录");
        var revision = await context.Local.Engine.Execute(plan.Id, token);
        Check.True(File.ReadAllText(Path.Combine(context.Target, "visible.txt")) == context.Source &&
            File.Exists(Path.Combine(context.Target, "gone", "cache", "keep.txt")) &&
            !File.Exists(Path.Combine(context.Target, "gone", "remove.txt")), "单向更新正常文件，保留忽略内容并删除普通子项");
        Check.True(!Directory.Exists(Path.Combine(revision.BackupPath!, "private")), "备份同样不读取忽略项");
        File.WriteAllText(Path.Combine(context.Target, "cache", "ignored.txt"), "ignored edits survive undo");
        await context.Local.Engine.Undo(plan.Id, token);
        Check.True(File.ReadAllText(Path.Combine(context.Target, "visible.txt")) == context.Target &&
            File.ReadAllText(Path.Combine(context.Target, "cache", "ignored.txt")) == "ignored edits survive undo",
            "撤销恢复参与同步的内容并保留忽略项的后续修改");
        using var client = context.Local.Client.Connect(context.Local.Client.Find(context.Profile.PeerId));
        using var forbidden = await client.GetAsync($"peer/file/{context.Profile.ShareId}?path=private/locked.txt&hash=unused&ticks=0", token);
        Check.True((int)forbidden.StatusCode == 403, "共享忽略目录拒绝直接远端下载");
    }

    private static async Task TwoWay(TwoWayContext context, CancellationToken token)
    {
        var profile = context.Profile with { Mode = SyncModes.TwoWay };
        context.Local.Store.Update(s => s with { Profiles = s.Profiles.Select(p => p.Id == profile.Id ? profile : p).ToArray() });
        File.WriteAllText(Path.Combine(context.Target, "visible.txt"), File.ReadAllText(Path.Combine(context.Source, "visible.txt")));
        File.WriteAllText(Path.Combine(context.Target, "upload.txt"), "upload");
        File.WriteAllText(Path.Combine(context.Source, "download.txt"), "download");
        var plan = await context.Local.Engine.Preview(profile.Id, token);
        await context.Local.Engine.Execute(plan.Id, token);
        Check.True(File.Exists(Path.Combine(context.Source, "upload.txt")) && File.Exists(Path.Combine(context.Target, "download.txt")) &&
            File.ReadAllText(Path.Combine(context.Target, "scratch.tmp")) == context.Target &&
            File.ReadAllText(Path.Combine(context.Source, "scratch.tmp")) == context.Source, "双向同步两端使用同一过滤范围，忽略内容保持独立");
        await context.Local.Engine.Undo(plan.Id, token);
        Check.True(!File.Exists(Path.Combine(context.Source, "upload.txt")) && !File.Exists(Path.Combine(context.Target, "download.txt")),
            "有忽略规则的双向同步能够同时撤销两端");
        var pending = await context.Local.Engine.Preview(profile.Id, token);
        await context.Local.Client.Prepare(profile, pending, token);
        context.Remote.Store.Update(s => s with { Shares = s.Shares.Select(share => share.Id == profile.ShareId ?
            share with { IgnorePatterns = "private/\nupload.txt" } : share).ToArray() });
        await Check.Reject(() => context.Local.Client.Control(new(profile, pending.Id, "commit"), token), "事务准备后新增共享忽略规则会阻止旧事务写入");
        await context.Local.Client.Control(new(profile, pending.Id, "recover"), token);
        context.Remote.Store.Update(s => s with { Shares = s.Shares.Select(share => share.Id == profile.ShareId ?
            share with { IgnorePatterns = "private/" } : share).ToArray() });
    }

    private static async Task RuleChanges(TwoWayContext context, CancellationToken token)
    {
        var initial = await context.Local.Engine.Preview(context.Profile.Id, token);
        await context.Local.Engine.Execute(initial.Id, token);
        var current = context.Local.Store.Current.Profiles.Single(p => p.Id == context.Profile.Id);
        var ignored = current with { IgnorePatterns = current.IgnorePatterns + "\nupload.txt" };
        context.Local.Store.Update(s => s with { Profiles = s.Profiles.Select(p => p.Id == ignored.Id ? ignored : p).ToArray() });
        File.WriteAllText(Path.Combine(context.Source, "upload.txt"), "remote ignored edit");
        var plan = await context.Local.Engine.Preview(ignored.Id, token);
        Check.True(!plan.Changes.Concat(plan.RemoteChanges).Any(c => c.Path == "upload.txt"), "新增忽略规则同时过滤历史基线，不误判为删除");
        await context.Local.Engine.Execute(plan.Id, token);
        context.Local.Store.Update(s => s with { Profiles = s.Profiles.Select(p => p.Id == current.Id ? current : p).ToArray() });
        var revealed = await context.Local.Engine.Preview(current.Id, token);
        Check.True(revealed.Conflicts.Contains("upload.txt"), "移除忽略规则后不同内容重新进入冲突预览");
    }
}
