namespace FileSync.Core;

public sealed record MergeResult(Manifest Manifest, IReadOnlyList<string> Conflicts);

public static class TwoWayPlanner
{
    public static MergeResult Merge(Manifest local, Manifest remote, Manifest? baseline)
    {
        var left = Index(local);
        var right = Index(remote);
        var previous = baseline is null ? new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase) : Index(baseline);
        var paths = left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase)
            .Union(previous.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        var entries = new List<Entry>();
        var conflicts = new List<string>();
        foreach (var path in paths)
        {
            var selection = Select(left.GetValueOrDefault(path), right.GetValueOrDefault(path),
                previous.GetValueOrDefault(path));
            if (selection.Conflict) conflicts.Add(path);
            if (selection.Entry is not null) entries.Add(selection.Entry);
        }
        conflicts.AddRange(ParentConflicts(entries));
        return new(new(Math.Max(local.RootModifiedTicks, remote.RootModifiedTicks), entries),
            conflicts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static Dictionary<string, Entry> Index(Manifest value) =>
        value.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);

    private static (Entry? Entry, bool Conflict) Select(Entry? left, Entry? right, Entry? previous)
    {
        if (SameContent(left, right)) return (Latest(left, right), false);
        if (SameContent(left, previous)) return (right, false);
        if (SameContent(right, previous)) return (left, false);
        return (left, true);
    }

    private static bool SameContent(Entry? first, Entry? second)
    {
        if (first is null || second is null) return first == second;
        return first.Path == second.Path && first.Directory == second.Directory &&
            (first.Directory || first.Hash == second.Hash && first.Length == second.Length);
    }

    private static Entry? Latest(Entry? first, Entry? second) =>
        first is null || second is null ? first ?? second : first.ModifiedTicks >= second.ModifiedTicks ? first : second;

    private static IEnumerable<string> ParentConflicts(IReadOnlyList<Entry> entries)
    {
        var index = entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var separator = entry.Path.LastIndexOf('/');
            if (separator < 0) continue;
            var parent = entry.Path[..separator];
            if (!index.TryGetValue(parent, out var directory) || !directory.Directory || directory.Path != parent)
                yield return entry.Path;
        }
    }
}
