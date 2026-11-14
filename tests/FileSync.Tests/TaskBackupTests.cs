using System.Net.Http.Json;
using System.Text.Json;
using FileSync.Core;

namespace FileSync.Tests;

public sealed record TaskBackupFixture(TestNode Local, TestNode Remote, IReadOnlyList<Profile> Profiles)
{
    public required string Root { get; init; }
}

public static class TaskBackupTests
{
    public const string Endpoint = "api/configuration/tasks/";

    public static async Task Run(SyncResourceContext nodes, CancellationToken token)
    {
        var localBefore = nodes.Local.Store.Current;
        var remoteBefore = nodes.Remote.Store.Current;
        try
        {
            var fixture = Initialize(nodes);
            var backup = await RoundTrip(fixture, token);
            await Updates(fixture, backup, token);
            await TaskBackupFaultTests.Run(fixture, backup, token);
            await UseRestoredTasks(fixture, token);
        }
        finally
        {
            nodes.Local.Store.Update(_ => localBefore);
            nodes.Remote.Store.Update(_ => remoteBefore);
        }
    }

    private static TaskBackupFixture Initialize(SyncResourceContext nodes)
    {
        var root = Path.Combine(nodes.Root, "任务备份 [中文]");
        var source = Path.Combine(root, "局域网来源");
        var localShare = Path.Combine(root, "本机共享");
        foreach (var name in new[] { "局域网来源", "本机共享", "单向目标", "双向目标", "OneDrive 资料", "其他任务" })
            Directory.CreateDirectory(Path.Combine(root, name));
        File.WriteAllText(Path.Combine(source, "真实资料.txt"), "配置恢复后继续传输");
        File.WriteAllText(Path.Combine(root, "OneDrive 资料", "原有资料.txt"), "导入配置不能改写资料");
        var share = new Share(StateStore.NewId(), "备份测试来源", source) { AllowWrite = true };
        nodes.Remote.Store.Update(s => s with { Shares = [share] });
        var oneWay = new Profile(StateStore.NewId(), "单向任务 [中文]", nodes.Remote.Identity.Id, share.Id,
            Path.Combine(root, "单向目标"), Path.Combine(root, "完整备份")) { SourceName = share.Name, IgnorePatterns = "*.tmp\n.git/" };
        var twoWay = oneWay with { Id = StateStore.NewId(), Name = "双向任务", Mode = SyncModes.TwoWay,
            TargetPath = Path.Combine(root, "双向目标"), BackupPath = null };
        var cloud = new Profile(StateStore.NewId(), "OneDrive 任务", "", "", Path.Combine(root, "OneDrive 资料"), null)
        {
            Transport = SyncTransports.OneDrive, Mode = SyncModes.Latest, RelayPath = Path.Combine(root, "云端仓库"),
            IgnorePatterns = "*.tmp", UploadIgnorePatterns = "*.private", PullIgnorePatterns = "/cache/"
        };
        var profiles = new[] { oneWay, twoWay, cloud };
        nodes.Local.Store.Update(s => s with { Profiles = profiles,
            Shares = [new(StateStore.NewId(), "导入前已发布的共享", localShare) { AllowWrite = true }] });
        return new(nodes.Local, nodes.Remote, profiles) { Root = root };
    }

    private static async Task<TaskConfigurationBackup> RoundTrip(TaskBackupFixture fixture, CancellationToken token)
    {
        var node = fixture.Local;
        var before = node.Store.Current;
        var identity = File.ReadAllBytes(Path.Combine(node.Store.Root, "identity.dpapi"));
        using var response = await node.Ui.GetAsync(Endpoint + "export", token);
        var bytes = await response.Content.ReadAsByteArrayAsync(token);
        var path = Path.Combine(fixture.Root, "全部同步任务.json");
        await File.WriteAllBytesAsync(path, bytes, token);
        await using var stream = File.OpenRead(path);
        var backup = await TaskBackupJson.Read<TaskConfigurationBackup>(stream, token);
        Check.True(response.IsSuccessStatusCode && response.Content.Headers.ContentDisposition?.DispositionType == "attachment" &&
            response.Content.Headers.ContentType?.MediaType == "application/json", "全部任务可通过下载接口导出为一个 JSON 文件");
        Check.True(backup.Format == TaskConfigurationBackup.FormatName && backup.ExportedUtc.Kind == DateTimeKind.Utc &&
            backup.Profiles.SequenceEqual(fixture.Profiles), "备份往返完整保留局域网单向、双向和 OneDrive 任务的所有配置字段");
        using var document = JsonDocument.Parse(bytes);
        Check.True(document.RootElement.EnumerateObject().Count() == 4 &&
            !document.RootElement.TryGetProperty("peers", out _) && !document.RootElement.TryGetProperty("shares", out _),
            "单文件只包含任务配置和格式信息，不导出配对授权、共享权限或设备私钥");
        foreach (var profile in fixture.Profiles)
        {
            using var deleted = await node.Ui.DeleteAsync($"api/profiles/{profile.Id}", token);
            Check.True(deleted.IsSuccessStatusCode, "测试中通过管理接口移除待恢复任务");
        }
        var other = fixture.Profiles[2] with { Id = StateStore.NewId(), Name = "导入前的其他任务",
            TargetPath = Path.Combine(fixture.Root, "其他任务"), RelayPath = Path.Combine(fixture.Root, "其他仓库") };
        node.Store.Update(s => s with { Profiles = [other] });
        var preview = await Preview(node, backup, token);
        Check.True(preview.Added == fixture.Profiles.Count && preview.Updated == 0 && node.Store.Current.Profiles.SequenceEqual([other]),
            "导入预览显示全部新增任务，预览本身不修改任务配置");
        var result = await Import(node, new(backup, preview.ConfigurationHash), token);
        Check.True(result.Added == fixture.Profiles.Count && fixture.Profiles.All(node.Store.Current.Profiles.Contains) &&
            node.Store.Current.Profiles.Contains(other), "单文件导入恢复全部任务并保留其他已有任务");
        Check.True(node.Store.Current.Name == before.Name && node.Store.Current.Peers.SequenceEqual(before.Peers) &&
            node.Store.Current.Shares.SequenceEqual(before.Shares) &&
            File.ReadAllBytes(Path.Combine(node.Store.Root, "identity.dpapi")).SequenceEqual(identity),
            "导入配置保持设备名称、现有配对、共享权限及设备身份不变");
        Check.True(new StateStore(node.Store.Root).Current.Profiles.SequenceEqual(node.Store.Current.Profiles),
            "恢复的全部任务已持久保存，重启后可重新读取");
        return backup;
    }

    private static async Task Updates(TaskBackupFixture fixture, TaskConfigurationBackup backup, CancellationToken token)
    {
        var node = fixture.Local;
        var preview = await Preview(node, backup, token);
        var repeated = await Import(node, new(backup, preview.ConfigurationHash), token);
        Check.True(repeated.Added == 0 && repeated.Updated == 0 && repeated.Unchanged == fixture.Profiles.Count &&
            node.Store.Current.Profiles.Count == fixture.Profiles.Count + 1, "重复导入相同备份不会生成重复任务");
        var changed = fixture.Profiles[0] with { Name = "临时更改任务名", IgnorePatterns = "*.cache" };
        node.Store.Update(s => s with { Profiles = s.Profiles.Select(p => p.Id == changed.Id ? changed : p).ToArray() });
        var update = await Preview(node, backup, token);
        Check.True(update.Updated == 1 && update.Added == 0, "同编号任务配置发生变化时，预览识别为更新");
        await Import(node, new(backup, update.ConfigurationHash), token);
        Check.True(node.Store.Current.Profiles.Contains(fixture.Profiles[0]), "按原编号恢复任务名称、忽略规则及其他设置");
    }

    private static async Task UseRestoredTasks(TaskBackupFixture fixture, CancellationToken token)
    {
        var oneWay = await fixture.Local.Engine.Preview(fixture.Profiles[0].Id, token);
        await fixture.Local.Engine.Execute(oneWay.Id, token);
        Check.True(File.ReadAllText(Path.Combine(fixture.Profiles[0].TargetPath, "真实资料.txt")) == "配置恢复后继续传输",
            "导入的局域网任务直接预览并完成真实 TLS 文件传输");
        await OneDriveTests.Publish(fixture.Local, fixture.Profiles[2], token);
        Check.True(File.ReadAllText(Path.Combine(fixture.Profiles[2].RelayPath!, "原有资料.txt")) == "导入配置不能改写资料",
            "导入的 OneDrive 任务可直接发布到原配置仓库");
    }

    public static async Task<TaskImportPreview> Preview(TestNode node, TaskConfigurationBackup backup, CancellationToken token)
    {
        using var response = await node.Ui.PostAsJsonAsync(Endpoint + "preview", backup, token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskImportPreview>(token))!;
    }

    public static async Task<TaskImportPreview> Import(TestNode node, TaskImportRequest request, CancellationToken token)
    {
        using var response = await node.Ui.PostAsJsonAsync(Endpoint + "import", request, token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskImportPreview>(token))!;
    }
}
