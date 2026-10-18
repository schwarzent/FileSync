using FileSync.Core;

namespace FileSync.Tests;

public sealed record SyncResourceContext(TestNode Local, TestNode Remote, string Root);

public sealed class SyncResourceWorkload(SyncResourceContext context)
{
    private const int StableFiles = 24;
    private const int PayloadBytes = 1024 * 1024;
    private readonly FileScanner scanner = new();
    private string Source => Path.Combine(context.Root, "连续同步源");
    private string Target => Path.Combine(context.Root, "连续同步目标");
    private Profile profile = null!;

    public async Task Initialize(CancellationToken token)
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Target);
        Directory.CreateDirectory(Path.Combine(Source, "空目录"));
        for (var index = 0; index < StableFiles; index++)
            await File.WriteAllTextAsync(Path.Combine(Source, $"稳定文件-{index:D2}.txt"), $"文件 {index}", token);
        var share = new Share(StateStore.NewId(), "连续同步验证", Source);
        context.Remote.Store.Update(settings => settings with { Shares = [share] });
        profile = new(StateStore.NewId(), "连续同步验证", context.Remote.Identity.Id, share.Id, Target, null);
        context.Local.Store.Update(settings => settings with { Profiles = [profile] });
    }

    public async Task<SyncPlan> Round(int iteration, CancellationToken token)
    {
        var payload = new byte[PayloadBytes];
        BitConverter.TryWriteBytes(payload, iteration);
        await File.WriteAllBytesAsync(Path.Combine(Source, "持续更新.bin"), payload, token);
        var plan = await context.Local.Engine.Preview(profile.Id, token);
        var revision = await context.Local.Engine.Execute(plan.Id, token);
        if (revision.Status != "completed" || !FileScanner.Equal(plan.Remote, await scanner.Scan(Target, token)))
            throw new InvalidOperationException($"连续同步第 {iteration} 轮内容或时间校验失败。");
        if (context.Local.Engine.Busy || context.Local.Engine.Progress.Phase != "idle")
            throw new InvalidOperationException($"连续同步第 {iteration} 轮未释放操作状态。");
        return plan;
    }
}
