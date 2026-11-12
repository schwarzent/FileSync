using FileSync.Core;
using FileSync.OneDrive;

namespace FileSync.Network;

public sealed class RemoteFiles(PeerClient peer, OneDriveFiles oneDrive) : IRemoteFiles, IRemoteTransactions
{
    private IRemoteFiles Source(Profile profile) => profile.Transport switch
    {
        SyncTransports.Lan => peer,
        SyncTransports.OneDrive => oneDrive,
        _ => throw new UserError("同步方式无效。")
    };

    public Task<Manifest> Manifest(Profile profile, CancellationToken token) => Source(profile).Manifest(profile, token);
    public Task Download(DownloadRequest request, CancellationToken token) => Source(request.Profile).Download(request, token);
    public Task Prepare(Profile profile, SyncPlan plan, CancellationToken token) => Transactions(profile).Prepare(profile, plan, token);
    public Task Upload(UploadRequest request, CancellationToken token) => Transactions(request.Profile).Upload(request, token);
    public Task Control(RemoteCommand command, CancellationToken token) => Transactions(command.Profile).Control(command, token);
    public Task<string> Status(Profile profile, string id, CancellationToken token) => Transactions(profile).Status(profile, id, token);

    private IRemoteTransactions Transactions(Profile profile) => profile.Transport == SyncTransports.Lan ? peer :
        throw new UserError("OneDrive 任务通过发布和接收同步。");
}
