using System.Diagnostics;

namespace FileSync.Tests;

public static class ResourceTests
{
    private sealed record ResourceSample(long ManagedBytes, int Handles);
    private static ResourceSample Sample()
    {
        using var process = Process.GetCurrentProcess();
        return new(GC.GetTotalMemory(true), process.HandleCount);
    }

    public static async Task Run(TestNode node, string peerId, CancellationToken token)
    {
        const int warmupRequests = 10;
        const int measuredRequests = 100;
        const long retainedMemoryAllowance = 8 * 1024 * 1024;
        const int handleAllowance = 32;
        for (var i = 0; i < warmupRequests; i++) await node.Client.Shares(peerId, token);
        var before = Sample();
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < measuredRequests; i++) await node.Client.Shares(peerId, token);
        var after = Sample();
        Console.WriteLine($"资源验证：{measuredRequests} 次真实 TLS 请求，{timer.ElapsedMilliseconds}ms，托管保留内存变化 {after.ManagedBytes - before.ManagedBytes} B，句柄变化 {after.Handles - before.Handles}");
        Check.True(after.ManagedBytes - before.ManagedBytes < retainedMemoryAllowance, "重复请求后托管保留内存无持续显著增长");
        Check.True(after.Handles - before.Handles < handleAllowance, "重复请求后句柄数量未随请求数线性增长");
    }
}
