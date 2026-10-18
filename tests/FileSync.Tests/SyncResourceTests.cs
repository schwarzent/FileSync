using System.Diagnostics;
using FileSync.Core;

namespace FileSync.Tests;

public static class SyncResourceTests
{
    private const int WarmupRounds = 4;
    private const int SampleGroups = 3;
    private const int RoundsPerGroup = 12;
    private const long ManagedAllowance = 8 * 1024 * 1024;
    private const long MainPrivateAllowance = 32 * 1024 * 1024;
    private const long TransportPrivateAllowance = 8 * 1024 * 1024;
    private const int HandleAllowance = 16;
    private sealed record Sample(long PrivateBytes, int Handles);

    public static async Task Run(SyncResourceContext context, CancellationToken token)
    {
        var workload = new SyncResourceWorkload(context);
        await workload.Initialize(token);
        for (var round = 0; round < WarmupRounds; round++) await workload.Round(round, token);
        using var current = Process.GetCurrentProcess();
        var transports = FindTransports(context);
        try { await Measure(new(workload, current, transports), token); }
        finally { foreach (var process in transports) process.Dispose(); }
    }

    private sealed record Measurement(SyncResourceWorkload Workload, Process Current, IReadOnlyList<Process> Transports);

    private static async Task Measure(Measurement measurement, CancellationToken token)
    {
        var processes = measurement.Transports.Prepend(measurement.Current).ToArray();
        var managed = new List<long>();
        var samples = new List<Sample[]>();
        var timer = Stopwatch.StartNew();
        for (var group = 0; group <= SampleGroups; group++)
        {
            managed.Add(GC.GetTotalMemory(true));
            samples.Add(processes.Select(Read).ToArray());
            if (group == SampleGroups) break;
            for (var round = 0; round < RoundsPerGroup; round++)
                await measurement.Workload.Round(WarmupRounds + group * RoundsPerGroup + round, token);
        }
        Console.WriteLine($"连续同步资源验证：{SampleGroups * RoundsPerGroup} 轮，每轮更新 1 MiB，{timer.ElapsedMilliseconds}ms");
        Console.WriteLine($"托管保留内存采样：{string.Join(", ", managed)} B");
        Check.True(managed[^1] - managed[1] < ManagedAllowance, "连续真实同步后托管保留内存未显著增长");
        for (var index = 0; index < processes.Length; index++)
            Verify(new(processes[index], index == 0), samples.Select(sample => sample[index]).ToArray());
    }

    private sealed record ObservedProcess(Process Process, bool Main);

    private static void Verify(ObservedProcess observed, IReadOnlyList<Sample> samples)
    {
        var name = observed.Main ? "主进程" : $"TLS 进程 {observed.Process.Id}";
        Console.WriteLine($"{name} 私有内存采样：{string.Join(", ", samples.Select(sample => sample.PrivateBytes))} B；句柄：{string.Join(", ", samples.Select(sample => sample.Handles))}");
        var allowance = observed.Main ? MainPrivateAllowance : TransportPrivateAllowance;
        Check.True(samples[^1].PrivateBytes - samples[1].PrivateBytes < allowance, $"{name}连续同步后私有内存未显著增长");
        Check.True(samples[^1].Handles - samples[1].Handles < HandleAllowance, $"{name}连续同步后句柄增量符合验证阈值");
    }

    private static Sample Read(Process process)
    {
        process.Refresh();
        return new(process.PrivateMemorySize64, process.HandleCount);
    }

    private static IReadOnlyList<Process> FindTransports(SyncResourceContext context)
    {
        var paths = new[] { context.Local.Store.Root, context.Remote.Store.Root }
            .Select(root => Directory.GetFiles(Path.Combine(root, "tools"), "*.exe").Single()).ToArray();
        var candidates = paths.Select(Path.GetFileNameWithoutExtension).Distinct()
            .SelectMany(Process.GetProcessesByName).ToArray();
        var processes = candidates.Where(process => paths.Contains(process.MainModule!.FileName,
            StringComparer.OrdinalIgnoreCase)).ToArray();
        foreach (var process in candidates.Except(processes)) process.Dispose();
        Check.True(processes.Length == paths.Length, "资源采样包含两端独立 TLS 通信进程");
        return processes;
    }
}
