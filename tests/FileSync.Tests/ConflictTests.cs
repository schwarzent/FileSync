using System.Net.Http.Json;
using FileSync.Core;

namespace FileSync.Tests;

public static class ConflictTests
{
    public static async Task Run(SyncResourceContext nodes, CancellationToken token)
    {
        DirectoryChoices(nodes.Root);
        var context = Configure(nodes);
        await Initial(context, token);
        await DeleteAndStructure(context, token);
        await Drift(context, token);
    }

    private static void DirectoryChoices(string target)
    {
        const long ticks = 100;
        var directory = new Entry("folder", true, 0, ticks, null);
        var child = new Entry("folder/child.txt", false, 1, ticks, new string('A', 64));
        var baseline = new Manifest(ticks, [directory, child]);
        var local = baseline with { Entries = [directory, child with { Hash = new string('B', 64) }] };
        var remote = new Manifest(ticks, []);
        var merge = TwoWayPlanner.Merge(local, remote, baseline);
        var profile = new Profile(StateStore.NewId(), "结构冲突", "peer", StateStore.NewId(), target, null) { Mode = SyncModes.TwoWay };
        var plan = new SyncPlan(StateStore.NewId(), profile, local, remote, [], DateTime.UtcNow)
            { Merged = merge.Manifest, Conflicts = merge.Conflicts };
        var keep = ConflictResolver.Resolve(plan, new Dictionary<string, string> { ["folder"] = "local" });
        var remove = ConflictResolver.Resolve(plan, new Dictionary<string, string> { ["folder"] = "remote" });
        Check.True(keep.Desired.Entries.SequenceEqual(local.Entries) && remove.Desired.Entries.Count == 0,
            "目录删除与子项修改冲突可选择完整保留或完整删除");
        var renamed = local with { Entries = [directory with { Path = "Folder" }, child with { Path = "Folder/child.txt" }] };
        var casing = TwoWayPlanner.Merge(local, renamed, null);
        var casePlan = plan with { Remote = renamed, Merged = casing.Manifest, Conflicts = casing.Conflicts };
        var chosen = ConflictResolver.Resolve(casePlan, casing.Conflicts.ToDictionary(p => p, _ => "remote"));
        Check.True(chosen.Desired.Entries.SequenceEqual(renamed.Entries), "大小写冲突选择同时采用父目录和子项名称");
    }

    private static TwoWayContext Configure(SyncResourceContext nodes)
    {
        var source = Path.Combine(nodes.Root, "choices-remote");
        var target = Path.Combine(nodes.Root, "choices-local");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        foreach (var root in new[] { source, target })
            foreach (var path in new[] { "local.txt", "remote.txt", "delete.txt" }) File.WriteAllText(Path.Combine(root, path), root);
        var share = new Share(StateStore.NewId(), "冲突选择", source) { AllowWrite = true };
        var profile = new Profile(StateStore.NewId(), "冲突选择", nodes.Remote.Identity.Id, share.Id, target, null)
            { Mode = SyncModes.TwoWay };
        nodes.Remote.Store.Update(s => s with { Shares = s.Shares.Append(share).ToArray() });
        nodes.Local.Store.Update(s => s with { Profiles = s.Profiles.Append(profile).ToArray() });
        return new(nodes.Local, nodes.Remote, profile) { Source = source };
    }

    private static async Task Initial(TwoWayContext context, CancellationToken token)
    {
        var plan = await context.Local.Engine.Preview(context.Profile.Id, token);
        Check.True(plan.Conflicts.Count == 3, "首次同名不同内容支持多个独立冲突");
        using var invalid = await context.Local.Ui.PostAsJsonAsync($"api/plans/{plan.Id}/resolve",
            new { choices = new Dictionary<string, string> { ["../outside"] = "local" } }, token);
        Check.True((int)invalid.StatusCode == 400, "冲突接口拒绝清单外路径");
        var partial = await context.Local.Engine.Resolve(plan.Id,
            new Dictionary<string, string> { ["local.txt"] = "local" }, token);
        Check.True(partial.Conflicts.Count == 2 && partial.Id != plan.Id, "单项选择生成独立预览并保留其他待选项");
        await Check.Reject(() => context.Local.Engine.Execute(partial.Id, token), "尚有未选择冲突时不能执行");
        using var response = await context.Local.Ui.PostAsJsonAsync($"api/plans/{partial.Id}/resolve", new
            { choices = new Dictionary<string, string> { ["remote.txt"] = "remote", ["delete.txt"] = "local" } }, token);
        Check.True(response.IsSuccessStatusCode, "管理接口接受混合方向的批量选择");
        var resolved = (await response.Content.ReadFromJsonAsync<SyncPlan>(token))!;
        Check.True(resolved.Conflicts.Count == 0 && resolved.Changes.Any(c => c.Path == "remote.txt") &&
            resolved.RemoteChanges.Any(c => c.Path == "local.txt"), "处理后的预览精确列出两个方向的修改");
        Check.True(File.ReadAllText(Path.Combine(context.Target, "remote.txt")) == context.Target &&
            File.ReadAllText(Path.Combine(context.Source, "local.txt")) == context.Source, "选择版本与应用选择均不写入同步文件");
        await context.Local.Engine.Execute(resolved.Id, token);
        Check.True(File.ReadAllText(Path.Combine(context.Source, "local.txt")) == context.Target &&
            File.ReadAllText(Path.Combine(context.Target, "remote.txt")) == context.Source, "执行真实 TLS 同步采用每项选择的本机或远端内容");
        await context.Local.Engine.Undo(resolved.Id, token);
        Check.True(File.ReadAllText(Path.Combine(context.Target, "remote.txt")) == context.Target &&
            File.ReadAllText(Path.Combine(context.Source, "local.txt")) == context.Source, "冲突选择后可撤销并恢复两端原始版本");
        var next = await context.Local.Engine.Preview(context.Profile.Id, token);
        var all = await context.Local.Engine.Resolve(next.Id, next.Conflicts.ToDictionary(p => p, _ => "remote"), token);
        await context.Local.Engine.Execute(all.Id, token);
        Check.True(File.ReadAllText(Path.Combine(context.Target, "local.txt")) == context.Source, "全部保留远端批量选择生效");
    }

    private static async Task DeleteAndStructure(TwoWayContext context, CancellationToken token)
    {
        var localDeleted = Path.Combine(context.Target, "delete.txt");
        File.Move(localDeleted, Path.Combine(Path.GetDirectoryName(context.Target)!, "retained-deleted.txt"));
        File.WriteAllText(Path.Combine(context.Source, "delete.txt"), "remote changed after baseline");
        var plan = await context.Local.Engine.Preview(context.Profile.Id, token);
        var resolved = await context.Local.Engine.Resolve(plan.Id, new Dictionary<string, string> { ["delete.txt"] = "local" }, token);
        await context.Local.Engine.Execute(resolved.Id, token);
        Check.True(!File.Exists(Path.Combine(context.Source, "delete.txt")), "删除与修改冲突可以明确选择已删除的一端");
        await context.Local.Engine.Undo(resolved.Id, token);
        Check.True(!File.Exists(localDeleted) && File.ReadAllText(Path.Combine(context.Source, "delete.txt")) == "remote changed after baseline",
            "删除冲突选择也可撤销并恢复远端改动");
        Directory.CreateDirectory(Path.Combine(context.Target, "folder"));
        File.WriteAllText(Path.Combine(context.Target, "folder", "child.txt"), "local subtree");
        File.WriteAllText(Path.Combine(context.Source, "folder"), "remote file");
        var structure = await context.Local.Engine.Preview(context.Profile.Id, token);
        Check.True(structure.Conflicts.Contains("folder") && !structure.Conflicts.Contains("folder/child.txt"), "文件与目录冲突按目录分组，不产生矛盾的子项选择");
        var selected = await context.Local.Engine.Resolve(structure.Id, structure.Conflicts.ToDictionary(p => p, _ => "local"), token);
        await context.Local.Engine.Execute(selected.Id, token);
        Check.True(File.ReadAllText(Path.Combine(context.Source, "folder", "child.txt")) == "local subtree", "全部保留本机支持文件替换为完整目录");
        await context.Local.Engine.Undo(selected.Id, token);
        var reverse = await context.Local.Engine.Preview(context.Profile.Id, token);
        var remote = await context.Local.Engine.Resolve(reverse.Id, reverse.Conflicts.ToDictionary(p => p, _ => "remote"), token);
        await context.Local.Engine.Execute(remote.Id, token);
        Check.True(File.ReadAllText(Path.Combine(context.Target, "folder")) == "remote file", "保留远端支持完整目录替换为文件");
    }

    private static async Task Drift(TwoWayContext context, CancellationToken token)
    {
        File.WriteAllText(Path.Combine(context.Target, "local.txt"), "new local");
        File.WriteAllText(Path.Combine(context.Source, "local.txt"), "new remote");
        var stale = await context.Local.Engine.Preview(context.Profile.Id, token);
        File.WriteAllText(Path.Combine(context.Target, "local.txt"), "edited after preview");
        await Check.Reject(() => context.Local.Engine.Resolve(stale.Id, stale.Conflicts.ToDictionary(p => p, _ => "local"), token),
            "选择冲突版本前重新检查两端状态，拒绝过期预览");
        var current = await context.Local.Engine.Preview(context.Profile.Id, token);
        var resolved = await context.Local.Engine.Resolve(current.Id, current.Conflicts.ToDictionary(p => p, _ => "local"), token);
        File.WriteAllText(Path.Combine(context.Source, "local.txt"), "edited after selection");
        await Check.Reject(() => context.Local.Engine.Execute(resolved.Id, token), "完成冲突选择后远端再修改仍会阻止覆盖");
        await context.Local.Engine.Recover(resolved.Id, token);
    }
}
