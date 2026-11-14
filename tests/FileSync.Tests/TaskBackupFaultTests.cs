using System.Net;
using System.Net.Http.Json;
using System.Text;
using FileSync.Core;

namespace FileSync.Tests;

public static class TaskBackupFaultTests
{
    public static async Task Run(TaskBackupFixture fixture, TaskConfigurationBackup backup, CancellationToken token)
    {
        await InvalidFiles(fixture, backup, token);
        await StaleAndBusy(fixture, backup, token);
        await Interrupted(fixture, backup, token);
        await Empty(fixture, backup, token);
    }

    private static async Task InvalidFiles(TaskBackupFixture fixture, TaskConfigurationBackup backup, CancellationToken token)
    {
        var profile = backup.Profiles[0];
        var invalid = new[]
        {
            backup with { Format = "other-application" }, backup with { Version = 0 },
            backup with { Profiles = null! }, backup with { Profiles = [null!] },
            backup with { Profiles = [profile, profile] },
            backup with { Profiles = [profile with { TargetPath = "../escape" }] },
            backup with { Profiles = [profile with { TargetPath = fixture.Local.Store.Root }] },
            backup with { Profiles = [profile with { IgnorePatterns = "!unsupported" }] },
            backup with { Profiles = [profile with { PeerId = new string('E', 64) }] },
            backup with { Profiles = [profile with { Mode = "unknown" }] },
            backup with { Profiles = [profile with { Name = "" }] },
            backup with { Profiles = [profile with { ShareId = "../share" }] }
        };
        var path = Path.Combine(fixture.Local.Store.Root, "settings.json");
        var original = File.ReadAllBytes(path);
        foreach (var value in invalid)
        {
            using var response = await fixture.Local.Ui.PostAsJsonAsync(TaskBackupTests.Endpoint + "preview", value, token);
            Check.True(response.StatusCode == HttpStatusCode.BadRequest, "无效配置备份在预览时被明确拒绝");
        }
        foreach (var json in new[] { "not-json", "null", "{}", "{\"format\":\"filesync-task-backup\",\"peers\":[]}" })
        {
            using var response = await fixture.Local.Ui.PostAsync(TaskBackupTests.Endpoint + "preview",
                new StringContent(json, Encoding.UTF8, "application/json"), token);
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(token);
            Check.True(response.StatusCode == HttpStatusCode.BadRequest && !string.IsNullOrEmpty(error?.Error),
                "损坏或缺少字段的 JSON 返回可读取的错误信息");
        }
        var preview = await TaskBackupTests.Preview(fixture.Local, backup, token);
        using var rejected = await fixture.Local.Ui.PostAsJsonAsync(TaskBackupTests.Endpoint + "import",
            new TaskImportRequest(backup with { Profiles = [profile with { TargetPath = "relative" }] }, preview.ConfigurationHash), token);
        Check.True(rejected.StatusCode == HttpStatusCode.BadRequest && File.ReadAllBytes(path).SequenceEqual(original),
            "导入执行阶段重新校验，失败不写入部分任务或损坏原配置");
        using var anonymous = new HttpClient { BaseAddress = fixture.Local.Ui.BaseAddress };
        using var denied = await anonymous.GetAsync(TaskBackupTests.Endpoint + "export", token);
        Check.True(denied.StatusCode == HttpStatusCode.Forbidden, "配置备份下载接口要求本机会话认证");
        Check.True(File.ReadAllText(Path.Combine(fixture.Profiles[2].TargetPath, "原有资料.txt")) == "导入配置不能改写资料",
            "导入和失败校验均不会修改已有资料文件");
    }

    private sealed record ErrorResponse(string Error);

    private static async Task StaleAndBusy(TaskBackupFixture fixture, TaskConfigurationBackup backup, CancellationToken token)
    {
        var node = fixture.Local;
        var preview = await TaskBackupTests.Preview(node, backup, token);
        var before = node.Store.Current;
        node.Store.Update(s => s with { Name = "预览后的设备名称修改" });
        using var stale = await node.Ui.PostAsJsonAsync(TaskBackupTests.Endpoint + "import", new TaskImportRequest(backup, preview.ConfigurationHash), token);
        Check.True(stale.StatusCode == HttpStatusCode.Conflict && node.Store.Current.Name == "预览后的设备名称修改",
            "预览后配置变化会使导入失效，保留后续修改");
        node.Store.Update(_ => before);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = node.Engine.Exclusive(async () => { started.SetResult(); await release.Task; return true; });
        await started.Task.WaitAsync(token);
        try
        {
            using var busy = await node.Ui.PostAsJsonAsync(TaskBackupTests.Endpoint + "import",
                new TaskImportRequest(backup, preview.ConfigurationHash), token);
            Check.True(busy.StatusCode == HttpStatusCode.Conflict, "同步互斥锁占用时不允许导入修改任务配置");
        }
        finally { release.SetResult(); await held; }
    }

    private static async Task Interrupted(TaskBackupFixture fixture, TaskConfigurationBackup backup, CancellationToken token)
    {
        var node = fixture.Local;
        var preview = await TaskBackupTests.Preview(node, backup, token);
        var revision = new Revision(StateStore.NewId(), fixture.Profiles[0].Id, "中断任务", "staging", DateTime.UtcNow, []);
        JsonFiles.Write(node.Store.RevisionPath(revision.Id), revision);
        try
        {
            using var response = await node.Ui.PostAsJsonAsync(TaskBackupTests.Endpoint + "import",
                new TaskImportRequest(backup, preview.ConfigurationHash), token);
            Check.True(response.StatusCode == HttpStatusCode.Conflict, "存在未恢复的同步事务时拒绝导入更改任务");
        }
        finally { JsonFiles.Write(node.Store.RevisionPath(revision.Id), revision with { Status = "failed" }); }
    }

    private static async Task Empty(TaskBackupFixture fixture, TaskConfigurationBackup backup, CancellationToken token)
    {
        var before = fixture.Local.Store.Current;
        var empty = backup with { Profiles = [] };
        var preview = await TaskBackupTests.Preview(fixture.Local, empty, token);
        var result = await TaskBackupTests.Import(fixture.Local, new(empty, preview.ConfigurationHash), token);
        Check.True(result.Added == 0 && result.Updated == 0 && result.Unchanged == 0 &&
            fixture.Local.Store.Current.Profiles.SequenceEqual(before.Profiles),
            "空任务备份可以导入，并保留所有已有任务");
    }
}
