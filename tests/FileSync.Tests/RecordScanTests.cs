using System.Net;
using System.Net.Http.Json;
using FileSync.Records;
using Microsoft.Extensions.DependencyInjection;

namespace FileSync.Tests;

public static class RecordScanTests
{
    public static async Task Run(RecordFixture fixture, CancellationToken token)
    {
        var node = fixture.Nodes.Local;
        var records = node.App.Services.GetRequiredService<RecordStore>();
        var before = records.Read(fixture.Local, node.Identity.Id)!.Current;
        var historyCount = node.Store.History().Count;
        var path = Path.Combine(fixture.Local.WorkspacePath, "共同文件.txt");
        await using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var response = await node.Ui.PostAsJsonAsync(
            $"{RecordTests.Endpoint}/{fixture.Local.Id}/generate", new { }, token);
        var body = await response.Content.ReadAsStringAsync(token);
        Check.True(response.StatusCode == HttpStatusCode.Conflict && body.Contains("独占或锁定"),
            "扫描接口对独占文件返回可读的占用原因，不作为服务器内部错误");
        var after = records.Read(fixture.Local, node.Identity.Id)!.Current;
        Check.True(before.Hash == after.Hash && before.Record.Id == after.Record.Id &&
            historyCount == node.Store.History().Count,
            "扫描失败保留上一份完整清单，且不创建虚假的同步版本记录");
    }
}
