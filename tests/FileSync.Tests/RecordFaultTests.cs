using System.Net;
using System.Net.Http.Json;
using FileSync.Core;
using FileSync.Records;
using Microsoft.Extensions.DependencyInjection;

namespace FileSync.Tests;

public static class RecordFaultTests
{
    public static async Task Run(RecordFixture fixture, CancellationToken token)
    {
        await CorruptAndSpoofed(fixture, token);
        await InvalidConfiguration(fixture, token);
        await NoScanAndMissingSnapshot(fixture, token);
    }

    private static async Task CorruptAndSpoofed(RecordFixture fixture, CancellationToken token)
    {
        var nodes = fixture.Nodes;
        var store = nodes.Local.App.Services.GetRequiredService<RecordStore>();
        var original = store.Read(fixture.Local, nodes.Local.Identity.Id)!;
        using var client = nodes.Local.Client.Connect(nodes.Local.Store.Current.Peers.Single());
        var endpoint = $"peer/record-repositories/{fixture.Local.RepositoryId}";
        using var damaged = await client.PostAsJsonAsync(endpoint,
            original with { Current = original.Current with { Hash = new string('0', 64) } }, token);
        Check.True(damaged.StatusCode == HttpStatusCode.Conflict, "局域网接收拒绝 SHA-256 被篡改的文件记录");
        var spoof = original.Current.Record with { DeviceId = nodes.Remote.Identity.Id };
        using var denied = await client.PostAsJsonAsync(endpoint,
            original with { Current = new(RecordJson.Hash(spoof), spoof) }, token);
        Check.True(denied.StatusCode == HttpStatusCode.Forbidden, "配对设备不能冒用另一设备的身份发布记录");
        var record = original.Current.Record;
        var malicious = record with { Manifest = record.Manifest with
            { Entries = [new("../escape", false, 1, DateTime.UtcNow.Ticks, new string('A', 64)) { CreatedTicks = DateTime.UtcNow.Ticks }] } };
        using var traversed = await client.PostAsJsonAsync(endpoint,
            original with { Current = new(RecordJson.Hash(malicious), malicious) }, token);
        Check.True(traversed.StatusCode == HttpStatusCode.BadRequest, "记录含路径穿越时即使哈希正确也拒绝保存");
        var changed = record with { Changes = [] };
        using var invalidChanges = await client.PostAsJsonAsync(endpoint,
            original with { Current = new(RecordJson.Hash(changed), changed) }, token);
        Check.True(invalidChanges.StatusCode == HttpStatusCode.Conflict, "记录的修改列表与父清单不一致时明确拒绝");
        using var anonymous = new HttpClient { BaseAddress = nodes.Local.Ui.BaseAddress };
        using var noSession = await anonymous.GetAsync($"{RecordTests.Endpoint}/{fixture.Local.Id}", token);
        Check.True(noSession.StatusCode == HttpStatusCode.Forbidden, "本机文件记录接口要求会话认证");
    }

    private static async Task InvalidConfiguration(RecordFixture fixture, CancellationToken token)
    {
        var node = fixture.Nodes.Local;
        var folder = Path.Combine(fixture.Root, "未使用的资料目录");
        Directory.CreateDirectory(folder);
        var requests = new[]
        {
            new RecordRepositoryRequest("无效仓库", folder) { RepositoryPath = folder },
            new RecordRepositoryRequest("无效仓库", folder) { RepositoryId = "../escape" },
            new RecordRepositoryRequest("无效仓库", folder) { IgnorePatterns = "!unsupported" },
            new RecordRepositoryRequest("无效仓库", folder) { PeerId = new string('F', 64) },
            new RecordRepositoryRequest("无效仓库", node.Store.Root),
            new RecordRepositoryRequest("重复仓库", fixture.Local.WorkspacePath)
        };
        foreach (var request in requests)
        {
            using var response = await node.Ui.PostAsJsonAsync(RecordTests.Endpoint, request, token);
            Check.True(response.StatusCode == HttpStatusCode.BadRequest, "无效记录仓库路径、过滤规则、设备或重复配置被拒绝");
        }
    }

    private static async Task NoScanAndMissingSnapshot(RecordFixture fixture, CancellationToken token)
    {
        var nodes = fixture.Nodes;
        var records = nodes.Local.App.Services.GetRequiredService<RecordStore>();
        var current = records.Read(fixture.Local, nodes.Local.Identity.Id)!;
        var originalHash = current.Current.Hash;
        var file = Path.Combine(fixture.Local.WorkspacePath, "共同文件.txt");
        File.WriteAllText(file, "记录生成之后的磁盘修改");
        var comparison = await RecordTests.Compare(nodes.Local, fixture.Local.Id, nodes.Remote.Identity.Id, token);
        Check.True(comparison.Local.RecordId == current.Current.Record.Id &&
            records.Read(fixture.Local, nodes.Local.Identity.Id)!.Current.Hash == originalHash,
            "仅比对记录使用已保存快照，不隐式重扫磁盘或改变快照");
        var path = Path.Combine(records.Root(fixture.Local), "snapshots", current.Previous!.Record.Id + ".json");
        var held = path + ".保留原文件";
        File.Move(path, held);
        try
        {
            using var missing = await nodes.Local.Ui.GetAsync($"{RecordTests.Endpoint}/{fixture.Local.Id}", token);
            Check.True(missing.StatusCode == HttpStatusCode.Conflict, "记录父快照未到齐时明确报错，不使用其他清单替代");
        }
        finally { File.Move(held, path); }
    }
}
