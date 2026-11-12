namespace FileSync.Core;

public static class LatestPlanner
{
    public static SyncPlan Merge(SyncPlan plan)
    {
        var local = plan.Local.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var remote = plan.Remote.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var entries = new List<Entry>();
        var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in local.Keys.Union(remote.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var selection = Select(local.GetValueOrDefault(path), remote.GetValueOrDefault(path));
            if (selection.Conflict) conflicts.Add(path);
            entries.Add(selection.Entry);
        }
        AddParentConflicts(entries, conflicts);
        var root = plan.Local.Entries.Count == 0 || plan.Remote.RootModifiedTicks >= plan.Local.RootModifiedTicks
            ? plan.Remote : plan.Local;
        var desired = root with { Entries = entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            IgnorePatterns = plan.Local.IgnorePatterns };
        var roots = conflicts.Where(p => !IgnoreRules.Ancestors(p).Any(conflicts.Contains)).ToArray();
        if (roots.Length == 0)
        {
            desired = SyncScope.Preserve(desired, plan.Local);
            FileScanner.Validate(desired, plan.Profile.TargetPath);
        }
        return plan with { Merged = desired, Conflicts = roots, Changes = Planner.Compare(plan.Local, desired) };
    }

    private static (Entry Entry, bool Conflict) Select(Entry? local, Entry? remote)
    {
        if (local is null || remote is null) return ((local ?? remote)!, false);
        return (remote.ModifiedTicks >= local.ModifiedTicks ? remote : local, Conflict(local, remote));
    }

    private static bool Conflict(Entry left, Entry right) => left.Directory != right.Directory ||
        left.Path != right.Path || !left.Directory && left.ModifiedTicks == right.ModifiedTicks &&
        (left.Hash != right.Hash || left.Length != right.Length);

    private static void AddParentConflicts(IReadOnlyList<Entry> entries, ISet<string> conflicts)
    {
        var index = entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var parent = IgnoreRules.Ancestors(entry.Path).FirstOrDefault();
            if (parent is null) continue;
            if (!index.TryGetValue(parent, out var directory) || !directory.Directory || directory.Path != parent)
                conflicts.Add(parent);
        }
    }
}
