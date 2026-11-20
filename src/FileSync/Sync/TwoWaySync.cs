using FileSync.Core;
using FileSync.Network;

namespace FileSync.Sync;

public sealed partial class SyncEngine
{
    private IRemoteTransactions Transactions => remote as IRemoteTransactions
        ?? throw new UserError("远端通信组件不支持双向同步。");

    private SyncPlan MergePlan(SyncPlan plan)
    {
        var last = store.History().FirstOrDefault(r => r.ProfileId == plan.Profile.Id && r.Status == "completed");
        var previous = last is null ? null : ReadArchivedPlan(last.Id);
        var baseline = previous?.Profile.Mode == SyncModes.TwoWay &&
            previous.Profile.PeerId == plan.Profile.PeerId && previous.Profile.ShareId == plan.Profile.ShareId &&
            previous.Profile.TargetPath == plan.Profile.TargetPath
                ? SyncScope.Filter(previous.Desired, plan.Local.IgnorePatterns) : null;
        var merged = TwoWayPlanner.Merge(plan.Local, plan.Remote, baseline);
        var desired = merged.Conflicts.Count > 0 ? merged.Manifest :
            SyncScope.Preserve(SyncScope.Preserve(merged.Manifest, plan.Local), plan.Remote);
        if (merged.Conflicts.Count == 0) FileScanner.Validate(desired, plan.Profile.TargetPath);
        return plan with { Merged = desired, Conflicts = merged.Conflicts,
            Changes = Planner.Compare(plan.Local, desired), RemoteChanges = Planner.Compare(plan.Remote, desired) };
    }

    private async Task<Revision> ApplyTwoWay(SyncPlan plan, Revision revision, CancellationToken token)
    {
        SetPhase("checking");
        await CheckUnchanged(plan, token);
        await Transactions.Prepare(plan.Profile, plan, token);
        await StageAll(plan, token);
        foreach (var entry in plan.RemoteChanges.Where(c => c.After is { Directory: false } && c.Kind != "metadata").Select(c => c.After!))
        {
            SetProgress(new("uploading", entry.Path, 0, entry.Length));
            await Transactions.Upload(new(plan.Profile, plan.Id, entry), token);
        }
        revision = await BackupLocal(plan, revision, token);
        await CheckUnchanged(plan, token);
        token.ThrowIfCancellationRequested();
        SetPhase("writing");
        Save(revision with { Status = "writing" });
        await Transactions.Control(new(plan.Profile, plan.Id, "commit"), CancellationToken.None);
        if (!FileScanner.Equal(await ScanLocal(plan, CancellationToken.None), plan.Local))
            throw new UserError("远端写入期间本机文件已改变，请恢复后重新预览。", 409);
        revision = revision with { Status = "writing", LocalWriteStarted = true };
        Save(revision);
        files.Archive(plan);
        files.Install(plan);
        await CheckDesired(plan, CancellationToken.None);
        return Save(revision with { Status = "completed" });
    }

    private async Task<Revision> BackupLocal(SyncPlan plan, Revision revision, CancellationToken token)
    {
        if (plan.Profile.BackupPath is null) return revision;
        SetPhase("backup");
        var destination = Path.Combine(plan.Profile.BackupPath, plan.Id);
        await TransactionFiles.Backup(plan, destination, token);
        return Save(revision with { BackupPath = destination });
    }

    private async Task CheckDesired(SyncPlan plan, CancellationToken token)
    {
        if (!FileScanner.Equal(await ScanLocal(plan, token), plan.Desired) ||
            !FileScanner.Equal(await remote.Manifest(plan.Profile, token), plan.RemoteDesired ?? plan.Desired))
            throw new UserError("双向同步后校验失败，请从版本记录恢复。", 409);
    }

    private async Task<Revision> UndoTwoWay(SyncPlan plan, Revision revision, CancellationToken token)
    {
        await CheckDesired(plan, token);
        await Transactions.Control(new(plan.Profile, plan.Id, "validate-undo"), token);
        Save(revision with { Status = "reverting" });
        SetPhase("writing");
        try
        {
            await Transactions.Control(new(plan.Profile, plan.Id, "undo"), CancellationToken.None);
            files.Rollback(plan);
            await CheckOriginal(plan, CancellationToken.None);
            return Save(revision with { Status = "reverted" });
        }
        finally { SetPhase("idle"); }
    }

    private async Task<Revision> RecoverTwoWay(SyncPlan plan, Revision revision, CancellationToken token)
    {
        var status = await Transactions.Status(plan.Profile, plan.Id, token);
        if (status is not ("absent" or "completed" or "writing" or "recovery-needed" or "reverting" or "staging" or "reverted"))
            throw new UserError("远端事务状态无法恢复，请检查远端版本记录。", 409);
        if (status == "completed") await Transactions.Control(new(plan.Profile, plan.Id, "validate-undo"), token);
        SetPhase("writing");
        try
        {
            if (status == "completed") await Transactions.Control(new(plan.Profile, plan.Id, "undo"), CancellationToken.None);
            else if (status is "writing" or "recovery-needed" or "reverting" or "staging")
                await Transactions.Control(new(plan.Profile, plan.Id, "recover"), CancellationToken.None);
            if (revision.LocalWriteStarted)
            {
                files.Rollback(plan);
                if (!FileScanner.Equal(await ScanLocal(plan, CancellationToken.None), plan.Local))
                    throw new UserError("本机恢复校验失败，请保留版本目录。", 409);
            }
            return Save(revision with { Status = "reverted", Error = null });
        }
        finally { SetPhase("idle"); }
    }

    private async Task CheckOriginal(SyncPlan plan, CancellationToken token)
    {
        if (!FileScanner.Equal(await ScanLocal(plan, token), plan.Local) ||
            !FileScanner.Equal(await remote.Manifest(plan.Profile, token), plan.Remote))
            throw new UserError("双向恢复校验失败，请保留两台设备的版本目录。", 409);
    }
}
