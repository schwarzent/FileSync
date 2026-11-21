using FileSync.Core;
using FileSync.Records;

namespace FileSync.Tests;

public static class RecordComparisonTests
{
    private const int HashLength = 64;
    private const int MinimumTickDifference = 1;
    private static readonly string RepositoryId = StateStore.NewId();
    private static readonly long ModifiedTicks = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc).Ticks;
    private static readonly Entry Original = new("文件.txt", false, 1, ModifiedTicks, new string('A', HashLength))
        { CreatedTicks = ModifiedTicks };
    private sealed record Expected(string Status, string? Side, string Message);

    public static void Run()
    {
        var newer = Original with { ModifiedTicks = ModifiedTicks + MinimumTickDifference, Hash = new string('B', HashLength) };
        Compare(Original, newer, new("other-newer", "other", "相差 100 纳秒也能准确识别对方文件较新"));
        Compare(newer, Original, new("local-newer", "local", "相差 100 纳秒也能准确识别本机文件较新"));
        Compare(Original, newer with { Hash = Original.Hash }, new("metadata", "other", "相同内容且修改时间不同也标明较新一端"));
        Compare(Original, newer with { ModifiedTicks = ModifiedTicks }, new("conflict", null, "同时间不同内容保留手动选择"));
        Compare(Original, null, new("local-only", null, "本机独有文件不自动选择较新版本"));
        Compare(null, Original, new("other-only", null, "对方独有文件不自动选择较新版本"));
        Compare(Original, newer with { Path = "文件.TXT" }, new("conflict", null, "名称大小写冲突不自动选择"));
        var directory = Original with { Directory = true, Length = 0, Hash = null };
        Compare(Original, directory, new("conflict", null, "文件与目录的类型冲突不自动选择"));
        Compare(directory, directory with { ModifiedTicks = newer.ModifiedTicks },
            new("metadata", null, "目录时间差异不纳入同名文件的批量操作"));
        Compare(Original, Original with { CreatedTicks = ModifiedTicks + MinimumTickDifference },
            new("metadata", null, "只有创建时间不同不视为修改版本更新"));
    }

    private static void Compare(Entry? local, Entry? other, Expected expected)
    {
        var result = RecordComparisonService.Compare(Record(local, 'A'), Record(other, 'B'));
        var row = result.Differences.Single();
        Check.True(row.Status == expected.Status && row.NewerSide == expected.Side, expected.Message);
    }

    private static WorkspaceRecord Record(Entry? entry, char device)
    {
        var manifest = new Manifest(ModifiedTicks, entry is null ? [] : [entry]);
        return new(RepositoryId, StateStore.NewId(), new string(device, HashLength))
        {
            DeviceName = device.ToString(), RecordedUtc = DateTime.UtcNow, Manifest = manifest,
            Changes = Planner.Compare(new(ModifiedTicks, []), manifest)
        };
    }
}
