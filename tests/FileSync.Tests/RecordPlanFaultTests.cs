using System.Net.Http.Json;
using FileSync.Core;
using FileSync.Records;
using Microsoft.Extensions.DependencyInjection;

namespace FileSync.Tests;

public static class RecordPlanFaultTests
{
    public static async Task Run(RecordPlanFixture fixture, RecordPlanDetails details, CancellationToken token)
    {
        var nodes = fixture.Nodes;
        using var denied = await nodes.Local.Ui.PostAsJsonAsync(RecordPlanTests.Endpoint(fixture.Local) + $"/{details.Plan.Id}/prepare",
            new RecordPlanConnection(nodes.Remote.Identity.Id), token);
        Check.True(!denied.IsSuccessStatusCode && (await denied.Content.ReadAsStringAsync(token)).Contains("允许局域网执行"),
            "未明确允许写入的资料目录不能执行方案");
        foreach (var pair in new[] { (Node: nodes.Local, Repository: fixture.Local), (Node: nodes.Remote, Repository: fixture.Other) })
        {
            using var response = await pair.Node.Ui.PostAsJsonAsync($"{RecordTests.Endpoint}/{pair.Repository.Id}/enable-lan", new { }, token);
            response.EnsureSuccessStatusCode();
        }
        await ChangedSelection(fixture, details, token);
        await AddedChild(fixture, details, token);
        await Interrupted(fixture, details, token);
        await InvalidPlans(fixture, details, token);
    }

    private static async Task ChangedSelection(RecordPlanFixture fixture, RecordPlanDetails details, CancellationToken token)
    {
        var node = fixture.Nodes.Local;
        var scanner = node.App.Services.GetRequiredService<FileScanner>();
        var before = await scanner.ScanMetadata(fixture.Local.WorkspacePath, token);
        var path = Path.Combine(fixture.Local.WorkspacePath, "冲突.txt");
        var text = File.ReadAllText(path);
        File.WriteAllText(path, "方案后选定文件已更新");
        using var result = await node.Ui.PostAsJsonAsync(RecordPlanTests.Endpoint(fixture.Local) + $"/{details.Plan.Id}/prepare",
            new RecordPlanConnection(fixture.Nodes.Remote.Identity.Id), token);
        Check.True((int)result.StatusCode == 409 && (await result.Content.ReadAsStringAsync(token)).Contains("冲突.txt") &&
            node.Store.History().Count == 0, "选定文件在清单后变化时明确停止，未开始传输或修改");
        File.WriteAllText(path, text);
        Sync.TransactionFiles.RestoreTimes(fixture.Local.WorkspacePath, before);
    }

    private static async Task AddedChild(RecordPlanFixture fixture, RecordPlanDetails details, CancellationToken token)
    {
        var node = fixture.Nodes.Remote;
        var scanner = node.App.Services.GetRequiredService<FileScanner>();
        var before = await scanner.ScanMetadata(fixture.Other.WorkspacePath, token);
        var child = Path.Combine(fixture.Other.WorkspacePath, "类型变更", "方案外新增.txt");
        File.WriteAllText(child, "必须保留的新文件");
        using var result = await fixture.Nodes.Local.Ui.PostAsJsonAsync(RecordPlanTests.Endpoint(fixture.Local) + $"/{details.Plan.Id}/prepare",
            new RecordPlanConnection(node.Identity.Id), token);
        Check.True(!result.IsSuccessStatusCode && File.ReadAllText(child) == "必须保留的新文件" && node.Store.History().Count == 0,
            "目录替换遇到方案外新增子项时停止，不把未选文件连带移走");
        File.Move(child, Path.Combine(fixture.Nodes.Root, "已验证的方案外新增.txt"));
        Sync.TransactionFiles.RestoreTimes(fixture.Other.WorkspacePath, before);
    }

    private static async Task Interrupted(RecordPlanFixture fixture, RecordPlanDetails details, CancellationToken token)
    {
        var node = fixture.Nodes.Local;
        var prepared = await RecordPlanTests.Prepare(node, new(fixture.Local.Id, details.Plan.Id, fixture.Nodes.Remote.Identity.Id), token);
        var peer = node.Store.Current.Peers.Single();
        node.Store.Update(s => s with { Peers = [peer with { Port = 1 }] });
        try { await Check.Reject(async () => await node.Engine.Execute(prepared.Id, token), "局域网中断时方案明确失败，不报告成功"); }
        finally { node.Store.Update(s => s with { Peers = [peer] }); }
        var recovered = await node.Engine.Recover(prepared.Id, token);
        Check.True(recovered.Status == "reverted", "未开始写入的中断方案可按真实远端事务状态恢复");
    }

    private static async Task InvalidPlans(RecordPlanFixture fixture, RecordPlanDetails details, CancellationToken token)
    {
        var node = fixture.Nodes.Local;
        var plan = details.Plan;
        var request = new RecordPlanRequest("无效方案", fixture.Nodes.Remote.Identity.Id)
        {
            LocalRecordId = plan.Local.Reference.Id, OtherRecordId = plan.Other.Reference.Id,
            Choices = new Dictionary<string, string> { ["../越界.txt"] = "local" }
        };
        using var traversal = await node.Ui.PostAsJsonAsync(RecordPlanTests.Endpoint(fixture.Local), request, token);
        using var direction = await node.Ui.PostAsJsonAsync(RecordPlanTests.Endpoint(fixture.Local), request with
            { Choices = new Dictionary<string, string> { ["冲突.txt"] = "invalid" } }, token);
        using var stale = await node.Ui.PostAsJsonAsync(RecordPlanTests.Endpoint(fixture.Local), request with
            { LocalRecordId = StateStore.NewId() }, token);
        Check.True(!traversal.IsSuccessStatusCode && !direction.IsSuccessStatusCode && (int)stale.StatusCode == 409,
            "拒绝越界路径、无效处理方向及预览之后已换版的清单引用");
    }
}
