using System.Net.Http.Json;
using FileSync.Core;
using FileSync.Records;
using Microsoft.Extensions.DependencyInjection;

namespace FileSync.Tests;

public sealed record RecordPlanFixture(SyncResourceContext Nodes, RecordRepository Local, RecordRepository Other);

public static class RecordPlanTests
{
    public static async Task Run(SyncResourceContext nodes, CancellationToken token)
    {
        var fixture = await Initialize(nodes, token);
        var details = await OfflinePlan(fixture, token);
        await RecordPlanFaultTests.Run(fixture, details, token);
        await ExecuteFromOther(fixture, details, token);
    }

    private static async Task<RecordPlanFixture> Initialize(SyncResourceContext nodes, CancellationToken token)
    {
        var root = Path.Combine(nodes.Root, "清单方案 [中文]");
        var left = Path.Combine(root, "资料A");
        var right = Path.Combine(root, "资料B");
        foreach (var path in new[] { left, right })
        {
            Directory.CreateDirectory(Path.Combine(path, "共同目录"));
            File.WriteAllText(Path.Combine(path, "共同目录", "选定.txt"), path);
            File.WriteAllText(Path.Combine(path, "共同目录", "未选.txt"), "未选内容" + path);
            File.WriteAllText(Path.Combine(path, "冲突.txt"), path);
            File.SetLastWriteTimeUtc(Path.Combine(path, "冲突.txt"), new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.WriteAllText(Path.Combine(path, "不记录.tmp"), path);
        }
        Directory.CreateDirectory(Path.Combine(left, "新目录"));
        File.WriteAllText(Path.Combine(left, "新目录", "子项.txt"), "A新增文件");
        File.WriteAllText(Path.Combine(right, "仅对方.txt"), "B新增文件");
        File.WriteAllText(Path.Combine(right, "待删除.txt"), "由方案明确删除");
        File.WriteAllText(Path.Combine(left, "类型变更"), "文件替换目录");
        Directory.CreateDirectory(Path.Combine(right, "类型变更"));
        File.WriteAllText(Path.Combine(right, "类型变更", "旧子项.txt"), "保留版本");
        nodes.Local.Store.Update(s => s with { Name = "电脑 A" });
        nodes.Remote.Store.Update(s => s with { Name = "电脑 B" });
        var local = await RecordTests.Create(nodes.Local, new("计划资料", left)
            { RepositoryPath = Path.Combine(root, "记录A"), IgnorePatterns = "*.tmp" }, token);
        var other = await RecordTests.Create(nodes.Remote, new("计划资料", right)
            { RepositoryPath = Path.Combine(root, "记录B"), RepositoryId = local.RepositoryId, IgnorePatterns = "*.tmp" }, token);
        await RecordTests.Generate(nodes.Local, local.Id, token);
        await RecordTests.Generate(nodes.Remote, other.Id, token);
        CopyRecords(new(nodes, local, other));
        return new(nodes, local, other);
    }

    private static async Task<RecordPlanDetails> OfflinePlan(RecordPlanFixture fixture, CancellationToken token)
    {
        var node = fixture.Nodes.Local;
        var peer = node.Store.Current.Peers.Single();
        node.Store.Update(s => s with { Peers = [peer with { Port = 1 }] });
        try
        {
            using var held = new FileStream(Path.Combine(fixture.Local.WorkspacePath, "冲突.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
            var comparison = await RecordTests.Compare(node, fixture.Local.Id, peer.Id, token);
            var request = Request(comparison);
            using var response = await node.Ui.PostAsJsonAsync(Endpoint(fixture.Local) + "/preview", request, token);
            response.EnsureSuccessStatusCode();
            var preview = (await response.Content.ReadFromJsonAsync<RecordPlanDetails>(token))!;
            Check.True(preview.Plan.Choices.Any(c => c.Path == "新目录") &&
                preview.Plan.Choices.Any(c => c.Path == "类型变更/旧子项.txt"), "目录子项和必需父目录在离线预览中明确列出");
            using var saved = await node.Ui.PostAsJsonAsync(Endpoint(fixture.Local), request, token);
            saved.EnsureSuccessStatusCode();
            var details = (await saved.Content.ReadFromJsonAsync<RecordPlanDetails>(token))!;
            Check.True(details.OtherChanges.Any(c => c.Path == "待删除.txt" && c.Kind == "delete") &&
                details.LocalChanges.Any(c => c.Path == "仅对方.txt" && c.Kind == "add"), "离线方案列明两个方向的文件新增、替换与显式删除");
            Check.True(!details.Plan.Choices.Any(c => c.Path.Contains("未选")) && node.Store.Current.Profiles.Count == 0,
                "未选差异保留，保存方案无需创建整目录同步任务");
            var reloaded = new RecordPlanStore(new RecordStore(new StateStore(node.Store.Root))).Read(fixture.Local, details.Plan.Id);
            Check.True(reloaded.Choices.SequenceEqual(details.Plan.Choices), "远端连接不可用且资料被独占时仍可比较、保存并重新加载方案");
            return details;
        }
        finally { node.Store.Update(s => s with { Peers = [peer] }); }
    }

    private static RecordPlanRequest Request(RecordComparison comparison) => new("仅处理选定文件", comparison.Other.DeviceId)
    {
        LocalRecordId = comparison.Local.RecordId, OtherRecordId = comparison.Other.RecordId,
        Choices = new Dictionary<string, string>
        {
            ["共同目录/选定.txt"] = "other", ["冲突.txt"] = "local", ["新目录/子项.txt"] = "local",
            ["仅对方.txt"] = "other", ["待删除.txt"] = "local", ["类型变更"] = "local"
        }
    };

    private static async Task ExecuteFromOther(RecordPlanFixture fixture, RecordPlanDetails details, CancellationToken token)
    {
        var nodes = fixture.Nodes;
        File.WriteAllText(Path.Combine(fixture.Local.WorkspacePath, "共同目录", "未选.txt"), "方案后 A 独立修改");
        File.WriteAllText(Path.Combine(fixture.Other.WorkspacePath, "共同目录", "未选.txt"), "方案后 B 独立修改");
        File.WriteAllText(Path.Combine(fixture.Other.WorkspacePath, "额外.txt"), "方案未提及的新增文件");
        var scanner = nodes.Local.App.Services.GetRequiredService<FileScanner>();
        var beforeA = await scanner.ScanMetadata(fixture.Local.WorkspacePath, token);
        var beforeB = await scanner.ScanMetadata(fixture.Other.WorkspacePath, token);
        CopyRecords(fixture);
        using var list = await nodes.Remote.Ui.GetAsync(Endpoint(fixture.Other), token);
        Check.True((await list.Content.ReadFromJsonAsync<RecordSyncPlan[]>(token))!.Any(p => p.Id == details.Plan.Id),
            "只复制记录文件即可在另一台电脑查看已保存方案");
        var prepared = await Prepare(nodes.Remote, new(fixture.Other.Id, details.Plan.Id, nodes.Local.Identity.Id), token);
        var selected = prepared.SelectedPaths!.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check.True(prepared.Changes.Concat(prepared.RemoteChanges).All(c => selected.Contains(c.Path)) &&
            !FileScanner.Equal(prepared.Desired, prepared.RemoteDesired!), "实际执行清单只含选定项目，两端未选内容各自保留");
        var revision = await nodes.Remote.Engine.Execute(prepared.Id, token);
        Check.True(revision.Status == "completed" && revision.RecordPlanId == details.Plan.Id,
            "可从方案另一端发起真实 TLS 双向选择传输，完整保存执行记录");
        await VerifyApplied(fixture, new(beforeA, beforeB), token);
        var reverted = await nodes.Remote.Engine.Undo(revision.Id, token);
        Check.True(reverted.Status == "reverted" &&
            FileScanner.Equal(beforeA, await scanner.ScanMetadata(fixture.Local.WorkspacePath, token)) &&
            FileScanner.Equal(beforeB, await scanner.ScanMetadata(fixture.Other.WorkspacePath, token)),
            "撤销方案可恢复两端原内容和原始创建、修改时间，未选差异继续保留");
    }

    private static async Task VerifyApplied(RecordPlanFixture fixture, SelectedManifests before, CancellationToken token)
    {
        var scanner = fixture.Nodes.Local.App.Services.GetRequiredService<FileScanner>();
        var afterA = await scanner.ScanMetadata(fixture.Local.WorkspacePath, token);
        var afterB = await scanner.ScanMetadata(fixture.Other.WorkspacePath, token);
        Check.True(File.ReadAllText(Path.Combine(fixture.Local.WorkspacePath, "仅对方.txt")) == "B新增文件" &&
            File.ReadAllText(Path.Combine(fixture.Other.WorkspacePath, "新目录", "子项.txt")) == "A新增文件" &&
            !File.Exists(Path.Combine(fixture.Other.WorkspacePath, "待删除.txt")) &&
            File.Exists(Path.Combine(fixture.Other.WorkspacePath, "类型变更")), "方案按记录的方向完成新增、明确删除和文件目录类型替换");
        var untouched = new[] { "共同目录/未选.txt", "不记录.tmp", "额外.txt" };
        var checks = new[] { new SelectedManifests(before.Local, afterA), new(before.Other, afterB) };
        Check.True(checks.All(pair => untouched.All(path =>
            pair.Local.Entries.FirstOrDefault(e => e.Path == path) == pair.Other.Entries.FirstOrDefault(e => e.Path == path))),
            "未选、忽略和方案后新增文件的内容、创建时间及修改时间全部保持原样");
    }

    public static string Endpoint(RecordRepository repository) => $"{RecordTests.Endpoint}/{repository.Id}/plans";

    public static async Task<SyncPlan> Prepare(TestNode node, RecordPlanExecutionRequest request, CancellationToken token)
    {
        var endpoint = $"{RecordTests.Endpoint}/{request.RepositoryId}/plans/{request.PlanId}/prepare";
        using var response = await node.Ui.PostAsJsonAsync(endpoint, new RecordPlanConnection(request.PeerId), token);
        if (!response.IsSuccessStatusCode) throw new Exception(await response.Content.ReadAsStringAsync(token));
        return (await response.Content.ReadFromJsonAsync<SyncPlan>(token))!;
    }

    public static void CopyRecords(RecordPlanFixture fixture)
    {
        var left = fixture.Nodes.Local.App.Services.GetRequiredService<RecordStore>().Root(fixture.Local);
        var right = fixture.Nodes.Remote.App.Services.GetRequiredService<RecordStore>().Root(fixture.Other);
        foreach (var pair in new[] { (Source: left, Target: right), (Source: right, Target: left) })
        {
            foreach (var file in Directory.EnumerateFiles(pair.Source, "*.json", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(pair.Target, Path.GetRelativePath(pair.Source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, true);
            }
        }
    }
}
