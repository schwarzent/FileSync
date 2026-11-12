namespace FileSync.Core;

public static class ConflictResolver
{
    public const string Local = "local";
    public const string Remote = "remote";

    public static SyncPlan Resolve(SyncPlan plan, IReadOnlyDictionary<string, string> choices)
    {
        if (plan.Profile.Mode != SyncModes.TwoWay || plan.Conflicts.Count == 0)
            throw new UserError("此预览没有待处理的双向冲突。", 409);
        var conflicts = plan.Conflicts.ToHashSet(StringComparer.Ordinal);
        if (choices is null || choices.Count == 0 || choices.Any(c => !conflicts.Contains(c.Key) || c.Value is not (Local or Remote)))
            throw new UserError("请选择有效的冲突路径及本机或远端版本。");
        var desired = SelectEntries(plan, choices);
        var remaining = plan.Conflicts.Where(path => !choices.ContainsKey(path)).ToArray();
        if (remaining.Length == 0)
        {
            desired = SyncScope.Preserve(SyncScope.Preserve(desired, plan.Local), plan.Remote);
            FileScanner.Validate(desired, plan.Profile.TargetPath);
        }
        return plan with { Id = StateStore.NewId(), CreatedUtc = DateTime.UtcNow, Merged = desired,
            Conflicts = remaining, Changes = Planner.Compare(plan.Local, desired),
            RemoteChanges = Planner.Compare(plan.Remote, desired) };
    }

    private static Manifest SelectEntries(SyncPlan plan, IReadOnlyDictionary<string, string> choices)
    {
        var index = choices.ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
        var entries = plan.Desired.Entries.Where(e => SelectedSide(e.Path, index) is null)
            .Concat(plan.Local.Entries.Where(e => SelectedSide(e.Path, index) == Local))
            .Concat(plan.Remote.Entries.Where(e => SelectedSide(e.Path, index) == Remote));
        return plan.Desired with { Entries = entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray() };
    }

    private static string? SelectedSide(string path, IReadOnlyDictionary<string, string> choices)
    {
        if (choices.TryGetValue(path, out var side)) return side;
        foreach (var parent in IgnoreRules.Ancestors(path))
            if (choices.TryGetValue(parent, out side)) return side;
        return null;
    }
}
