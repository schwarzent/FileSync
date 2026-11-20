using FileSync.Core;

namespace FileSync.Sync;

public static class SelectedFileTimes
{
    public static Manifest Scope(SyncPlan plan, Manifest manifest)
    {
        if (plan.SelectedPaths is null) return manifest;
        var changed = plan.Changes.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parents = changed.SelectMany(IgnoreRules.Ancestors).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return manifest with { Entries = manifest.Entries.Where(e => changed.Contains(e.Path) ||
            e.Directory && parents.Contains(e.Path)).ToArray() };
    }

    public static void Validate(SyncPlan plan)
    {
        if (plan.SelectedPaths is null) return;
        var selected = plan.SelectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (plan.Changes.Any(c => !selected.Contains(c.Path))) throw new UserError("事务包含方案之外的文件修改。", 409);
        foreach (var change in plan.Changes.Where(c => c.Before is { Directory: true } && TransactionFiles.ReplacesContent(c)))
        {
            if (plan.Local.Entries.Any(e => e.Path.StartsWith(change.Path + "/", StringComparison.OrdinalIgnoreCase) && !selected.Contains(e.Path)))
                throw new UserError($"目录包含未列入方案的子项，不能删除或替换：{change.Path}", 409);
        }
    }
}
