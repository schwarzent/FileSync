using FileSync.Core;

namespace FileSync.Sync;

public sealed partial class SyncEngine
{
    public Task<SyncPlan> Resolve(string id, IReadOnlyDictionary<string, string> choices, CancellationToken token) =>
        Exclusive(async () =>
        {
            EnsureRecovered();
            var plan = ReadPlan(id);
            if (!store.Current.Profiles.Contains(plan.Profile)) throw new UserError("任务配置已变更，请重新预览。", 409);
            if (File.Exists(store.RevisionPath(id))) throw new UserError("此预览已经执行，请重新预览。", 409);
            SetPhase("checking");
            try
            {
                await CheckUnchanged(plan, token);
                var resolved = ConflictResolver.Resolve(plan, choices);
                JsonFiles.Write(store.PlanPath(resolved.Id), resolved);
                return resolved;
            }
            finally { SetPhase("idle"); }
        });
}
