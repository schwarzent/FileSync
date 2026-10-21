using System.Net.Http.Json;
using FileSync.Core;

namespace FileSync.Network;

public sealed record RemotePreparation(string ShareId, Manifest Before, Manifest After);
public sealed record UploadRequest(Profile Profile, string PlanId, Entry Entry);
public sealed record RemoteCommand(Profile Profile, string Id, string Action);
public sealed record TransactionState(string Status);

public interface IRemoteTransactions
{
    Task Prepare(Profile profile, SyncPlan plan, CancellationToken token);
    Task Upload(UploadRequest request, CancellationToken token);
    Task Control(RemoteCommand command, CancellationToken token);
    Task<string> Status(Profile profile, string id, CancellationToken token);
}

public sealed partial class PeerClient : IRemoteTransactions
{
    public async Task Prepare(Profile profile, SyncPlan plan, CancellationToken token)
    {
        using var client = Connect(Find(profile.PeerId));
        using var response = await client.PostAsJsonAsync(TransactionUrl(plan.Id),
            new RemotePreparation(profile.ShareId, plan.Remote, plan.Desired), token);
        await Check(response, token);
    }

    public async Task Upload(UploadRequest request, CancellationToken token)
    {
        using var client = Connect(Find(request.Profile.PeerId));
        var path = PathPolicy.Resolve(request.Profile.TargetPath, request.Entry.Path);
        await using var stream = FileScanner.OpenRead(path);
        using var content = new StreamContent(stream, FileScanner.BufferSize);
        var url = TransactionUrl(request.PlanId) + "/file?path=" + Uri.EscapeDataString(request.Entry.Path);
        using var response = await client.PostAsync(url, content, token);
        await Check(response, token);
    }

    public async Task Control(RemoteCommand command, CancellationToken token)
    {
        using var client = Connect(Find(command.Profile.PeerId));
        using var response = await client.PostAsJsonAsync(TransactionUrl(command.Id) + "/" + command.Action, new { }, token);
        await Check(response, token);
    }

    public async Task<string> Status(Profile profile, string id, CancellationToken token)
    {
        using var client = Connect(Find(profile.PeerId));
        using var response = await client.GetAsync(TransactionUrl(id), token);
        await Check(response, token);
        return (await response.Content.ReadFromJsonAsync<TransactionState>(token)
            ?? throw new UserError("远端事务状态为空。")).Status;
    }

    private static string TransactionUrl(string id) => "peer/transactions/" + StateStore.SafeId(id);
}
