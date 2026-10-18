namespace FileSync.Core;

public static class Planner
{
    public static IReadOnlyList<Change> Compare(Manifest local, Manifest remote)
    {
        var before = local.Entries.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var after = remote.Entries.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        return before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(path => Describe(before.GetValueOrDefault(path), after.GetValueOrDefault(path)))
            .OfType<Change>().ToArray();
    }

    private static Change? Describe(Entry? before, Entry? after)
    {
        if (before is null) return new("add", after!.Path, null, after);
        if (after is null) return new("delete", before.Path, before, null);
        if (before.Path != after.Path) return new("replace", after.Path, before, after);
        if (before.Directory != after.Directory) return new("replace", after.Path, before, after);
        if (!after.Directory && before.Hash != after.Hash) return new("update", after.Path, before, after);
        if (before.ModifiedTicks != after.ModifiedTicks)
            return new("metadata", after.Path, before, after);
        return null;
    }
}
