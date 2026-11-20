using FileSync.Core;
using FileSync.OneDrive;

namespace FileSync.Sync;

public sealed partial class SyncEngine
{
    public Task<PublishResult> Publish(string profileId, OneDrivePublisher publisher, CancellationToken token) =>
        Exclusive(async () =>
        {
            EnsureRecovered();
            store.Validate(store.Current);
            var profile = store.Current.Profiles.FirstOrDefault(p => p.Id == profileId)
                ?? throw new UserError("同步任务不存在。");
            if (profile.Transport != SyncTransports.OneDrive) throw new UserError("此任务未使用 OneDrive 中继。");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Volatile.Write(ref activeCancellation, cancellation);
            SetPhase("scanning");
            try { return await publisher.Publish(profile, cancellation.Token, SetProgress); }
            finally { Volatile.Write(ref activeCancellation, null); SetPhase("idle"); }
        });

    private Task<Manifest> ScanProfile(Profile profile, CancellationToken token, string patterns) =>
        profile.Transport == SyncTransports.OneDrive || profile.PreserveCreationTime ? scanner.ScanMetadata(profile.TargetPath, token, patterns) :
            scanner.Scan(profile.TargetPath, token, patterns);

    public Task<Snapshot> RecoverPublication(string profileId, CancellationToken token) => Exclusive(async () =>
    {
        EnsureRecovered();
        store.Validate(store.Current);
        var profile = store.Current.Profiles.FirstOrDefault(p => p.Id == profileId)
            ?? throw new UserError("同步任务不存在。", 404);
        if (profile.Transport != SyncTransports.OneDrive) throw new UserError("此任务没有 OneDrive 仓库。");
        token.ThrowIfCancellationRequested();
        SetPhase("writing");
        try { return await RepositoryWorkspace.Recover(profile, scanner, CancellationToken.None); }
        finally { SetPhase("idle"); }
    });
}
