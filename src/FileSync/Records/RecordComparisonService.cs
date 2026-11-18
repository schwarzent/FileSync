using FileSync.Core;

namespace FileSync.Records;

public static class RecordComparisonService
{
    public static RecordComparison Compare(WorkspaceRecord local, WorkspaceRecord other)
    {
        var left = local.Manifest.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var right = other.Manifest.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var leftChanges = local.Changes.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var rightChanges = other.Changes.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var rows = left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase)
            .Union(leftChanges.Keys, StringComparer.OrdinalIgnoreCase).Union(rightChanges.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(path =>
        {
            var a = left.GetValueOrDefault(path);
            var b = right.GetValueOrDefault(path);
            return new RecordDifference(path, Status(a, b)) { Local = a, Other = b,
                LocalChange = leftChanges.GetValueOrDefault(path)?.Kind, OtherChange = rightChanges.GetValueOrDefault(path)?.Kind };
        }).ToArray();
        return new(RecordJson.Describe(local), RecordJson.Describe(other), rows.Count(r => r.Status == "same"))
        {
            Differences = rows.Where(r => r.Status != "same").ToArray(),
            DifferentRules = local.Manifest.IgnorePatterns != other.Manifest.IgnorePatterns
        };
    }

    private static string Status(Entry? local, Entry? other)
    {
        if (local is null || other is null) return Missing(local, other);
        if (local.Directory != other.Directory || local.Path != other.Path) return "conflict";
        if (SameContent(local, other)) return SameTimes(local, other) ? "same" : "metadata";
        if (local.ModifiedTicks == other.ModifiedTicks) return "conflict";
        return local.ModifiedTicks > other.ModifiedTicks ? "local-newer" : "other-newer";
    }

    private static string Missing(Entry? local, Entry? other)
    {
        if (local is null && other is null) return "same";
        return local is null ? "other-only" : "local-only";
    }

    private static bool SameContent(Entry local, Entry other) =>
        local.Directory || local.Hash == other.Hash && local.Length == other.Length;
    private static bool SameTimes(Entry local, Entry other) =>
        local.ModifiedTicks == other.ModifiedTicks && local.CreatedTicks == other.CreatedTicks;
}
