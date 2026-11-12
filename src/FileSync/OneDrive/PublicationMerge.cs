using FileSync.Core;

namespace FileSync.OneDrive;

public static class PublicationMerge
{
    public static SyncPlan Resolve(StateStore store, SyncPlan plan)
    {
        if (plan.Conflicts.Count == 0) return plan;
        var revision = store.History().FirstOrDefault(r => r.ProfileId == plan.Profile.Id && r.Status == "completed");
        var previous = revision is null ? null : JsonFiles.Read<SyncPlan>(Path.Combine(store.VaultPath(revision.Id), "plan.json"));
        var choices = AcceptedChoices(plan, previous);
        var resolved = choices.Count == 0 ? plan : ConflictResolver.Resolve(plan, choices);
        if (resolved.Conflicts.Count > 0)
            throw new UserError("本机与 OneDrive 存在同时间异内容或目录结构冲突，请先预览接收并处理冲突，再发布。", 409);
        return resolved;
    }

    private static IReadOnlyDictionary<string, string> AcceptedChoices(SyncPlan plan, SyncPlan? previous)
    {
        if (previous?.Profile != plan.Profile) return new Dictionary<string, string>();
        return plan.Conflicts.Where(path => Subtree(plan.Local, path).SequenceEqual(Subtree(previous.Desired, path)) &&
            Subtree(plan.Remote, path).SequenceEqual(Subtree(previous.Remote, path)))
            .ToDictionary(path => path, _ => ConflictResolver.Local, StringComparer.Ordinal);
    }

    private static IEnumerable<Entry> Subtree(Manifest manifest, string path) => manifest.Entries
        .Where(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase) || e.Path.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))
        .OrderBy(e => e.Path, StringComparer.Ordinal);
}
