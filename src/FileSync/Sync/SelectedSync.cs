using FileSync.Core;
using FileSync.Records;

namespace FileSync.Sync;

public sealed partial class SyncEngine
{
    public Task<SyncPlan> PrepareSelection(RecordPlanExecution execution, RecordPlanExecutionRequest request,
        CancellationToken token) => Exclusive(async () =>
    {
        EnsureRecovered();
        SetPhase("scanning");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Volatile.Write(ref activeCancellation, cancellation);
        try
        {
            var plan = await execution.Prepare(request, cancellation.Token);
            JsonFiles.Write(store.PlanPath(plan.Id), plan);
            return plan;
        }
        finally { Volatile.Write(ref activeCancellation, null); SetPhase("idle"); }
    });

    private void ValidateSelectionConfiguration(SyncPlan plan)
    {
        var repository = store.Current.RecordRepositories.FirstOrDefault(r => r.Id == plan.Profile.Id)
            ?? throw new UserError("方案对应的本机文件清单配置已移除。", 409);
        if (repository.WorkspacePath != plan.Profile.TargetPath || repository.IgnorePatterns != plan.Profile.IgnorePatterns ||
            !store.Current.Peers.Any(p => p.Id == plan.Profile.PeerId))
            throw new UserError("方案执行配置已改变，请重新检查。", 409);
    }
}
