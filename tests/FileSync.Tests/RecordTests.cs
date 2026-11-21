using System.Net.Http.Json;
using FileSync.Core;
using FileSync.Records;
using Microsoft.Extensions.DependencyInjection;

namespace FileSync.Tests;

public sealed record RecordFixture(SyncResourceContext Nodes, string Root)
{
    public required RecordRepository Local { get; init; }
    public required RecordRepository Remote { get; init; }
}

public static class RecordTests
{
    public const string Endpoint = "api/record-repositories";
    private const int LargeFileSize = 2 * 1024 * 1024;

    public static async Task Run(SyncResourceContext nodes, CancellationToken token)
    {
        var beforeLocal = nodes.Local.Store.Current;
        var beforeRemote = nodes.Remote.Store.Current;
        try
        {
            var fixture = await Initialize(nodes, token);
            await LocalAndLan(fixture, token);
            await RecordScanTests.Run(fixture, token);
            await Changes(fixture, token);
            await RecordFaultTests.Run(fixture, token);
            await SharedFolder(nodes, token);
        }
        finally
        {
            nodes.Local.Store.Update(_ => beforeLocal);
            nodes.Remote.Store.Update(_ => beforeRemote);
        }
    }

    private static async Task<RecordFixture> Initialize(SyncResourceContext nodes, CancellationToken token)
    {
        var root = Path.Combine(nodes.Root, "独立文件记录 [中文]");
        var a = Path.Combine(root, "电脑A");
        var b = Path.Combine(root, "电脑B");
        foreach (var folder in new[] { a, b })
        {
            Directory.CreateDirectory(Path.Combine(folder, "空目录"));
            File.WriteAllText(Path.Combine(folder, "共同文件.txt"), "原始共同内容");
            File.WriteAllText(Path.Combine(folder, "冲突.txt"), folder);
            File.SetLastWriteTimeUtc(Path.Combine(folder, "冲突.txt"), new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }
        File.WriteAllBytes(Path.Combine(a, "大型文件.bin"), new byte[LargeFileSize]);
        File.WriteAllText(Path.Combine(a, "仅A.txt"), "A独立新增");
        File.WriteAllText(Path.Combine(b, "仅B.txt"), "B独立新增");
        File.WriteAllText(Path.Combine(a, "忽略.tmp"), "不记录");
        nodes.Local.Store.Update(s => s with { Name = "电脑A" });
        nodes.Remote.Store.Update(s => s with { Name = "电脑B" });
        var local = await Create(nodes.Local, new("工作资料", a) { IgnorePatterns = "*.tmp" }, token);
        using var list = await nodes.Remote.Ui.GetAsync($"{Endpoint}/peers/{nodes.Local.Identity.Id}", token);
        Check.True(list.IsSuccessStatusCode && (await list.Content.ReadAsStringAsync(token)).Contains(local.RepositoryId),
            "已配对设备通过真实 TLS 发现可加入的记录仓库");
        var remote = await Create(nodes.Remote, new("工作资料", b)
            { RepositoryId = local.RepositoryId, PeerId = nodes.Local.Identity.Id, IgnorePatterns = "*.tmp" }, token);
        return new(nodes, root) { Local = local, Remote = remote };
    }

    private static async Task LocalAndLan(RecordFixture fixture, CancellationToken token)
    {
        var nodes = fixture.Nodes;
        var local = await Generate(nodes.Local, fixture.Local.Id, token);
        var remote = await Generate(nodes.Remote, fixture.Remote.Id, token);
        Check.True(local.Bytes >= LargeFileSize && remote.Files > 0, "两端可独立生成含大小、原始时间及 SHA-256 的文件记录");
        var store = nodes.Local.App.Services.GetRequiredService<RecordStore>();
        var package = store.Read(fixture.Local, nodes.Local.Identity.Id)!;
        Check.True(package.Current.Record.Manifest.RootCreatedTicks is not null &&
            package.Current.Record.Manifest.Entries.All(e => e.CreatedTicks is not null) &&
            package.Current.Record.Manifest.Entries.Any(e => e.Directory && e.Path == "空目录") &&
            package.Current.Record.Manifest.Entries.All(e => e.Path != "忽略.tmp"),
            "记录保留根目录、空目录和创建时间，忽略规则不读取排除文件");
        Check.True(Directory.EnumerateFiles(store.Root(fixture.Local), "*", SearchOption.AllDirectories)
            .All(path => Path.GetExtension(path) == ".json"), "本机记录仓库只包含 JSON 元数据，不保存资料内容对象");
        using var heldA = new FileStream(Path.Combine(fixture.Local.WorkspacePath, "共同文件.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        using var heldB = new FileStream(Path.Combine(fixture.Remote.WorkspacePath, "共同文件.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        using var exchanged = await nodes.Remote.Ui.PostAsJsonAsync($"{Endpoint}/{fixture.Remote.Id}/exchange",
            new RecordExchangeRequest(nodes.Local.Identity.Id), token);
        Check.True(exchanged.IsSuccessStatusCode, "资料文件被独占锁定时仍能完成双向 TLS 记录交换，不扫描或上传资料");
        var comparison = await Compare(nodes.Local, fixture.Local.Id, nodes.Remote.Identity.Id, token);
        Check.True(comparison.Differences.Any(r => r.Path == "仅A.txt" && r.Status == "local-only") &&
            comparison.Differences.Any(r => r.Path == "仅B.txt" && r.Status == "other-only") &&
            comparison.Differences.Any(r => r.Path == "冲突.txt" && r.Status == "conflict"),
            "只读保存的记录即可识别两端独有文件及同时间不同内容冲突");
        var metadataBytes = Directory.EnumerateFiles(store.Root(fixture.Local), "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
        Check.True(metadataBytes < LargeFileSize / 10, "交换后仓库空间仅为文件清单大小，未复制大型资料文件");
    }

    private static async Task Changes(RecordFixture fixture, CancellationToken token)
    {
        var nodes = fixture.Nodes;
        var before = File.ReadAllBytes(Path.Combine(fixture.Local.WorkspacePath, "仅A.txt"));
        File.WriteAllText(Path.Combine(fixture.Local.WorkspacePath, "共同文件.txt"), "A更新后的内容");
        File.WriteAllText(Path.Combine(fixture.Remote.WorkspacePath, "仅B.txt"), "B更新后的内容");
        File.Move(Path.Combine(fixture.Local.WorkspacePath, "仅A.txt"), Path.Combine(fixture.Root, "A移除后保留原内容.txt"));
        await Generate(nodes.Local, fixture.Local.Id, token);
        await Generate(nodes.Remote, fixture.Remote.Id, token);
        using var exchanged = await nodes.Remote.Ui.PostAsJsonAsync($"{Endpoint}/{fixture.Remote.Id}/exchange",
            new RecordExchangeRequest(nodes.Local.Identity.Id), token);
        exchanged.EnsureSuccessStatusCode();
        var comparison = await Compare(nodes.Local, fixture.Local.Id, nodes.Remote.Identity.Id, token);
        Check.True(comparison.Differences.Any(r => r.Path == "共同文件.txt" && r.Status == "local-newer" && r.LocalChange == "update"),
            "记录比较标明电脑 A 本次更新及原始修改时间较新的一端");
        var records = nodes.Local.App.Services.GetRequiredService<RecordStore>();
        var current = records.Read(fixture.Local, nodes.Local.Identity.Id)!;
        Check.True(current.Previous is not null && current.Current.Record.Changes.Any(c => c.Path == "仅A.txt" && c.Kind == "delete") &&
            current.Current.Record.Parent?.Hash == current.Previous.Hash, "每台设备的连续记录保留父哈希、修改前后元数据和删除信息");
        Check.True(File.ReadAllBytes(Path.Combine(fixture.Root, "A移除后保留原内容.txt")).SequenceEqual(before) &&
            !File.Exists(Path.Combine(fixture.Remote.WorkspacePath, "大型文件.bin")), "生成、交换及比对记录均不修改另一端资料");
        Check.True(new StateStore(nodes.Local.Store.Root).Current.RecordRepositories.Any(r => r.Id == fixture.Local.Id),
            "记录仓库配置已持久保存，重新读取后仍可使用");
        using var repeated = await nodes.Remote.Ui.PostAsJsonAsync($"{Endpoint}/{fixture.Remote.Id}/exchange",
            new RecordExchangeRequest(nodes.Local.Identity.Id), token);
        Check.True(repeated.IsSuccessStatusCode, "重复交换相同记录不创建文件内容或破坏历史快照");
    }

    private static async Task SharedFolder(SyncResourceContext nodes, CancellationToken token)
    {
        nodes.Local.Store.Update(s => s with { RecordRepositories = [] });
        nodes.Remote.Store.Update(s => s with { RecordRepositories = [] });
        var root = Path.Combine(nodes.Root, "记录中继");
        var a = Path.Combine(root, "资料A");
        var b = Path.Combine(root, "资料B");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "文件.txt"), "A");
        File.WriteAllText(Path.Combine(b, "文件.txt"), "B");
        var local = await Create(nodes.Local, new("云端元数据", a) { RepositoryPath = Path.Combine(root, "云端A") }, token);
        var remote = await Create(nodes.Remote, new("云端元数据", b)
            { RepositoryId = local.RepositoryId, RepositoryPath = Path.Combine(root, "云端B") }, token);
        await Generate(nodes.Local, local.Id, token);
        await Generate(nodes.Remote, remote.Id, token);
        var records = nodes.Local.App.Services.GetRequiredService<RecordStore>();
        CopyRecords(records.Root(local), records.Root(remote));
        CopyRecords(records.Root(remote), records.Root(local));
        using var held = new FileStream(Path.Combine(a, "文件.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        var comparison = await Compare(nodes.Local, local.Id, nodes.Remote.Identity.Id, token);
        Check.True(comparison.Differences.Any(r => r.Path == "文件.txt"), "两份独立记录目录经真实文件复制后即可离线比对，不需要局域网连接或上传资料");
        Check.True(!File.Exists(Path.Combine(local.RepositoryPath!, "文件.txt")) &&
            Directory.EnumerateFiles(records.Root(local), "*", SearchOption.AllDirectories).All(p => Path.GetExtension(p) == ".json"),
            "OneDrive 记录目录仅保存元数据，原文件不会写入中继");
    }

    private static void CopyRecords(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*.json", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }

    public static async Task<RecordRepository> Create(TestNode node, RecordRepositoryRequest request, CancellationToken token)
    {
        using var response = await node.Ui.PostAsJsonAsync(Endpoint, request, token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecordRepository>(token))!;
    }
    public static async Task<RecordDevice> Generate(TestNode node, string id, CancellationToken token)
    {
        using var response = await node.Ui.PostAsJsonAsync($"{Endpoint}/{id}/generate", new { }, token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecordDevice>(token))!;
    }
    public static async Task<RecordComparison> Compare(TestNode node, string id, string other, CancellationToken token) =>
        (await node.Ui.GetFromJsonAsync<RecordComparison>($"{Endpoint}/{id}/compare?deviceId={other}", token))!;
}
