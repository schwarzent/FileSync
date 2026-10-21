using FileSync.Core;
using FileSync.Network;

namespace FileSync.Sync;

public sealed record IncomingRequest(string Id, string Owner);
public sealed record IncomingUpload(IncomingRequest Request, string Path, Stream Body);
public sealed record IncomingServices(StateStore Store, FileScanner Scanner, TransactionFiles Files);

public sealed class IncomingTransactions(IncomingServices services, SyncEngine engine)
{
    private readonly StateStore store = services.Store;
    private readonly FileScanner scanner = services.Scanner;
    private readonly TransactionFiles files = services.Files;

    public Task<Revision> Prepare(IncomingRequest request, RemotePreparation preparation, CancellationToken token) => engine.Exclusive(async () =>
    {
        engine.EnsureRecovered();
        var share = Writable(preparation.ShareId);
        var id = StateStore.SafeId(request.Id);
        if (File.Exists(store.RevisionPath(id))) throw new UserError("事务编号已存在。", 409);
        FileScanner.Validate(preparation.Before, share.Path);
        FileScanner.Validate(preparation.After, share.Path);
        if (!FileScanner.Equal(await scanner.Scan(share.Path, token), preparation.Before))
            throw new UserError("远端文件夹自预览后已改变，请重新预览。", 409);
        var profile = new Profile(share.Id, "双向接收：" + share.Name, request.Owner, share.Id, share.Path, null);
        var plan = new SyncPlan(id, profile, preparation.Before, preparation.After,
            Planner.Compare(preparation.Before, preparation.After), DateTime.UtcNow) { OwnerPeerId = request.Owner };
        Directory.CreateDirectory(store.VaultPath(id));
        JsonFiles.Write(Path.Combine(store.VaultPath(id), "plan.json"), plan);
        return Save(new(id, profile.Id, profile.Name, "staging", DateTime.UtcNow, plan.Changes));
    });

    public Task<Revision> Upload(IncomingUpload upload, CancellationToken token) => engine.Exclusive(async () =>
    {
        var plan = Read(upload.Request);
        Writable(plan.Profile.ShareId);
        RequireStatus(plan.Id, "staging");
        var entry = plan.Changes.FirstOrDefault(c => c.Path == upload.Path && c.Kind != "metadata" && c.After is { Directory: false })?.After
            ?? throw new UserError("上传文件不在事务清单中。");
        var destination = files.Stage(plan, entry);
        await WriteUpload(new(upload.Body, destination, entry), token);
        return JsonFiles.Read<Revision>(store.RevisionPath(plan.Id));
    });

    private sealed record UploadFile(Stream Source, string Destination, Entry Entry);

    private static async Task WriteUpload(UploadFile upload, CancellationToken token)
    {
        await using (var destination = new FileStream(upload.Destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, FileScanner.BufferSize, FileOptions.Asynchronous))
        {
            var buffer = new byte[FileScanner.BufferSize];
            long received = 0;
            int count;
            while ((count = await upload.Source.ReadAsync(buffer, token)) > 0)
            {
                received += count;
                if (received > upload.Entry.Length) throw new UserError("上传文件超过清单声明的大小。", 409);
                await destination.WriteAsync(buffer.AsMemory(0, count), token);
            }
            await destination.FlushAsync(token);
            destination.Flush(true);
        }
        await TransactionFiles.VerifyFile(upload.Destination, upload.Entry, token);
    }

    public Task<Revision> Commit(IncomingRequest request, CancellationToken token) => engine.Exclusive(async () =>
    {
        var plan = Read(request);
        Writable(plan.Profile.ShareId);
        var revision = RequireStatus(plan.Id, "staging");
        if (!FileScanner.Equal(await scanner.Scan(plan.Profile.TargetPath, token), plan.Local))
            throw new UserError("远端文件夹在传输期间已改变，请恢复后重新预览。", 409);
        foreach (var entry in plan.Changes.Where(c => c.After is { Directory: false } && c.Kind != "metadata").Select(c => c.After!))
            await TransactionFiles.VerifyFile(files.Stage(plan, entry), entry, token);
        token.ThrowIfCancellationRequested();
        Save(revision with { Status = "writing", LocalWriteStarted = true });
        files.Archive(plan);
        files.Install(plan);
        if (!FileScanner.Equal(await scanner.Scan(plan.Profile.TargetPath, CancellationToken.None), plan.Remote))
            throw new UserError("远端写入校验失败，请恢复中断事务。", 409);
        return Save(revision with { Status = "completed", LocalWriteStarted = true });
    });

    public Task<TransactionState> Status(IncomingRequest request) => engine.Exclusive(() =>
    {
        if (!File.Exists(store.RevisionPath(request.Id))) return Task.FromResult(new TransactionState("absent"));
        Read(request);
        return Task.FromResult(new TransactionState(JsonFiles.Read<Revision>(store.RevisionPath(request.Id)).Status));
    });

    public Task<TransactionState> ValidateUndo(IncomingRequest request, CancellationToken token) => engine.Exclusive(async () =>
    {
        var plan = Read(request);
        RequireStatus(plan.Id, "completed");
        var latest = store.History().FirstOrDefault(r => r.ProfileId == plan.Profile.Id && r.Status == "completed");
        if (latest?.Id != plan.Id || !FileScanner.Equal(await scanner.Scan(plan.Profile.TargetPath, token), plan.Remote))
            throw new UserError("远端已有后续同步或文件修改，无法撤销。", 409);
        return new TransactionState("completed");
    });

    public async Task<Revision> Undo(IncomingRequest request, CancellationToken token)
    {
        Read(request);
        return await engine.Undo(request.Id, token);
    }

    public async Task<Revision> Recover(IncomingRequest request, CancellationToken token)
    {
        Read(request);
        if (JsonFiles.Read<Revision>(store.RevisionPath(request.Id)).Status == "staging")
            return await engine.Exclusive(() => Task.FromResult(Save(RequireStatus(request.Id, "staging") with { Status = "reverted" })));
        return await engine.Recover(request.Id, token);
    }

    private Share Writable(string id) => store.Current.Shares.FirstOrDefault(s => s.Id == id && s.AllowWrite)
        ?? throw new UserError("该共享未允许双向写入，请在远端设备启用。", 403);

    private SyncPlan Read(IncomingRequest request)
    {
        var plan = JsonFiles.Read<SyncPlan>(Path.Combine(store.VaultPath(request.Id), "plan.json"));
        if (plan.OwnerPeerId != request.Owner) throw new UserError("此事务属于其他设备。", 403);
        return plan;
    }

    private Revision RequireStatus(string id, string status)
    {
        var revision = JsonFiles.Read<Revision>(store.RevisionPath(id));
        if (revision.Status != status) throw new UserError("事务状态已改变，请查看版本记录。", 409);
        return revision;
    }

    private Revision Save(Revision revision)
    {
        JsonFiles.Write(store.RevisionPath(revision.Id), revision);
        return revision;
    }
}
