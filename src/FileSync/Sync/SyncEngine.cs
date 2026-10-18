using FileSync.Core;
using FileSync.Network;

namespace FileSync.Sync;

public sealed class SyncEngine(StateStore store, FileScanner scanner, IRemoteFiles remote, TransactionFiles files)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private SyncProgress progress = new("idle", null, 0, 0);
    public SyncProgress Progress => Volatile.Read(ref progress);
    public bool Busy => gate.CurrentCount == 0;

    public async Task<T> Exclusive<T>(Func<Task<T>> action)
    {
        if (!await gate.WaitAsync(0)) throw new UserError("当前有操作正在进行，请等待完成。", 409);
        try { return await action(); }
        finally { gate.Release(); }
    }

    public Task<SyncPlan> Preview(string profileId, CancellationToken token) => Exclusive(async () =>
    {
        EnsureRecovered();
        var profile = store.Current.Profiles.FirstOrDefault(p => p.Id == profileId)
            ?? throw new UserError("同步任务不存在。");
        SetProgress("scanning");
        try
        {
            var local = await scanner.Scan(profile.TargetPath, token);
            var source = await remote.Manifest(profile, token);
            FileScanner.Validate(source, profile.TargetPath);
            var plan = new SyncPlan(StateStore.NewId(), profile, local, source,
                Planner.Compare(local, source), DateTime.UtcNow);
            JsonFiles.Write(store.PlanPath(plan.Id), plan);
            return plan;
        }
        finally { SetProgress("idle"); }
    });

    public Task<Revision> Execute(string planId, CancellationToken token) => Exclusive(async () =>
    {
        EnsureRecovered();
        var plan = ReadPlan(planId);
        if (!store.Current.Profiles.Contains(plan.Profile)) throw new UserError("任务配置已变更，请重新预览。", 409);
        if (File.Exists(store.RevisionPath(plan.Id))) throw new UserError("此预览已经执行，请重新预览。", 409);
        var revision = new Revision(plan.Id, plan.Profile.Id, plan.Profile.Name, "staging", DateTime.UtcNow, plan.Changes);
        Directory.CreateDirectory(store.VaultPath(plan.Id));
        JsonFiles.Write(Path.Combine(store.VaultPath(plan.Id), "plan.json"), plan);
        Save(revision);
        try { return await Apply(plan, revision, token); }
        catch (Exception error)
        {
            var state = JsonFiles.Read<Revision>(store.RevisionPath(plan.Id));
            Save(state with { Status = state.Status == "writing" ? "recovery-needed" : "failed", Error = error.Message });
            throw;
        }
        finally { SetProgress("idle"); }
    });

    private async Task<Revision> Apply(SyncPlan plan, Revision revision, CancellationToken token)
    {
        SetProgress("checking");
        await CheckUnchanged(plan, token);
        await StageAll(plan, token);
        if (plan.Profile.BackupPath is not null)
        {
            SetProgress("backup");
            var backup = Path.Combine(plan.Profile.BackupPath, plan.Id);
            await TransactionFiles.Backup(plan, backup, token);
            revision = revision with { BackupPath = backup };
            Save(revision);
        }
        await CheckUnchanged(plan, token);
        token.ThrowIfCancellationRequested();
        SetProgress("writing");
        Save(revision with { Status = "writing" });
        files.Archive(plan);
        files.Install(plan);
        var actual = await scanner.Scan(plan.Profile.TargetPath, CancellationToken.None);
        if (!FileScanner.Equal(actual, plan.Remote)) throw new UserError("同步后校验失败，版本记录中可恢复同步前状态。", 409);
        return Save(revision with { Status = "completed" });
    }

    private async Task StageAll(SyncPlan plan, CancellationToken token)
    {
        var entries = plan.Changes.Where(c => c.After is { Directory: false } && c.Kind != "metadata")
            .Select(c => c.After!).ToArray();
        var total = entries.Sum(e => e.Length);
        long transferred = 0;
        foreach (var entry in entries)
        {
            var path = files.Stage(plan, entry);
            SetProgress("downloading", entry.Path, transferred, total);
            await remote.Download(new(plan.Profile, entry, path, bytes =>
            {
                transferred += bytes;
                SetProgress("downloading", entry.Path, transferred, total);
            }), token);
            await TransactionFiles.VerifyFile(path, entry, token);
        }
    }

    private async Task CheckUnchanged(SyncPlan plan, CancellationToken token)
    {
        if (!FileScanner.Equal(await scanner.Scan(plan.Profile.TargetPath, token), plan.Local))
            throw new UserError("本机文件夹自预览后已改变，请重新预览。", 409);
        if (!FileScanner.Equal(await remote.Manifest(plan.Profile, token), plan.Remote))
            throw new UserError("远端文件夹自预览后已改变，请重新预览。", 409);
    }

    public Task<Revision> Undo(string id, CancellationToken token) => Exclusive(async () =>
    {
        EnsureRecovered();
        var revision = JsonFiles.Read<Revision>(store.RevisionPath(id));
        if (revision.Status != "completed") throw new UserError("仅能撤销已完成的同步。", 409);
        var latest = store.History().FirstOrDefault(r => r.ProfileId == revision.ProfileId && r.Status == "completed");
        if (latest?.Id != id) throw new UserError("请先撤销此任务最近一次同步。", 409);
        var plan = ReadArchivedPlan(id);
        if (!FileScanner.Equal(await scanner.Scan(plan.Profile.TargetPath, token), plan.Remote))
            throw new UserError("同步后本机文件已改变，停止撤销以保护现有修改。", 409);
        Save(revision with { Status = "reverting" });
        files.Rollback(plan);
        if (!FileScanner.Equal(await scanner.Scan(plan.Profile.TargetPath, token), plan.Local))
            throw new UserError("恢复校验失败，请保留版本文件并检查目标文件夹。", 409);
        return Save(revision with { Status = "reverted" });
    });

    public Task<Revision> Recover(string id, CancellationToken token) => Exclusive(async () =>
    {
        var revision = JsonFiles.Read<Revision>(store.RevisionPath(id));
        if (revision.Status is not ("writing" or "recovery-needed" or "reverting" or "staging"))
            throw new UserError("此记录无需恢复。");
        var plan = ReadArchivedPlan(id);
        if (revision.Status != "staging") files.Rollback(plan);
        if (!FileScanner.Equal(await scanner.Scan(plan.Profile.TargetPath, token), plan.Local))
            throw new UserError("恢复后的文件与原始清单不一致，请检查版本目录。", 409);
        return Save(revision with { Status = "reverted", Error = null });
    });

    private void EnsureRecovered()
    {
        if (store.History().Any(r => r.Status is "writing" or "recovery-needed" or "reverting" or "staging"))
            throw new UserError("有中断的同步，请先在版本记录中恢复。", 409);
    }

    private SyncPlan ReadPlan(string id) => JsonFiles.Read<SyncPlan>(store.PlanPath(id));
    private SyncPlan ReadArchivedPlan(string id) => JsonFiles.Read<SyncPlan>(Path.Combine(store.VaultPath(id), "plan.json"));
    private Revision Save(Revision revision)
    {
        JsonFiles.Write(store.RevisionPath(revision.Id), revision);
        return revision;
    }
    private void SetProgress(string phase, string? path = null, long bytes = 0, long total = 0) =>
        Volatile.Write(ref progress, new(phase, path, bytes, total));
}
