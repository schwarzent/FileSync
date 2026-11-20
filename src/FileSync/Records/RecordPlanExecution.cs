using System.Net.Http.Json;
using FileSync.Core;
using FileSync.Network;
using FileSync.Security;

namespace FileSync.Records;

public sealed record RecordExecutionEnvironment(StateStore State, FileScanner Scanner, DeviceIdentity Identity);
public sealed record RecordExecutionServices(RecordPlanService Plans, RecordService Records, PeerClient Peers);

public sealed class RecordPlanExecution(RecordExecutionServices services, RecordExecutionEnvironment environment)
{
    private readonly RecordPlanService plans = services.Plans;
    private readonly RecordService records = services.Records;
    private readonly PeerClient peers = services.Peers;

    public async Task<SyncPlan> Prepare(RecordPlanExecutionRequest request, CancellationToken token)
    {
        var repository = records.Find(request.RepositoryId);
        var plan = plans.Details(request.RepositoryId, request.PlanId).Plan;
        var ownId = environment.Identity.Id;
        var left = plan.Local.Device.DeviceId == ownId;
        if (!left && plan.Other.Device.DeviceId != ownId) throw new UserError("本机不在此方案的两台设备中。", 403);
        var otherId = left ? plan.Other.Device.DeviceId : plan.Local.Device.DeviceId;
        if (request.PeerId != otherId) throw new UserError("所选局域网设备与方案设备不一致。", 409);
        environment.State.Validate(environment.State.Current);
        var share = await Connection(repository, otherId, token);
        var profile = new Profile(repository.Id, plan.Name, otherId, share.ShareId, repository.WorkspacePath, null)
            { Mode = SyncModes.TwoWay, IgnorePatterns = repository.IgnorePatterns, PreserveCreationTime = true };
        var remote = await peers.Manifest(profile, token);
        FileScanner.Validate(remote, repository.WorkspacePath);
        SyncScope.Validate(remote);
        var local = await environment.Scanner.ScanMetadata(repository.WorkspacePath, token, remote.IgnorePatterns);
        var current = left ? new SelectedManifests(local, remote) : new SelectedManifests(remote, local);
        var saved = plans.Manifests(repository, plan);
        CheckSelected(plan, saved, current);
        var after = RecordSelection.Apply(plan, current, saved);
        ValidateSelection(new(current.Local, after.Local), plan, repository.WorkspacePath);
        ValidateSelection(new(current.Other, after.Other), plan, repository.WorkspacePath);
        var desired = left ? after.Local : after.Other;
        var remoteDesired = left ? after.Other : after.Local;
        return new(StateStore.NewId(), profile, local, remote, Planner.Compare(local, desired), DateTime.UtcNow)
        {
            Merged = desired, RemoteDesired = remoteDesired, RemoteChanges = Planner.Compare(remote, remoteDesired),
            SelectedPaths = plan.Choices.Select(c => c.Path).ToArray(), RecordPlanId = plan.Id
        };
    }

    private async Task<RecordShareConnection> Connection(RecordRepository repository, string peerId, CancellationToken token)
    {
        using var client = peers.Connect(peers.Find(peerId));
        using var response = await client.GetAsync($"peer/record-repositories/{repository.RepositoryId}/connection", token);
        await PeerClient.Check(response, token);
        var connection = await response.Content.ReadFromJsonAsync<RecordShareConnection>(token)
            ?? throw new UserError("对方未提供局域网方案连接。");
        if (connection.DeviceId != peerId) throw new UserError("对方清单设备身份与配对身份不一致。", 403);
        return connection;
    }

    private static void CheckSelected(RecordSyncPlan plan, SelectedManifests saved, SelectedManifests current)
    {
        var pairs = new[] { new SelectedManifests(saved.Local, current.Local), new(saved.Other, current.Other) };
        foreach (var pair in pairs)
        {
            var expected = pair.Local.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
            var actual = pair.Other.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var choice in plan.Choices)
            {
                var before = expected.GetValueOrDefault(choice.Path);
                var now = actual.GetValueOrDefault(choice.Path);
                var equal = before is { Directory: true } && now is { Directory: true } ? before.Path == now.Path : before == now;
                if (!equal) throw new UserError($"方案中的文件自生成清单后已改变，请更新清单并重新制定方案：{choice.Path}", 409);
            }
        }
    }

    private static void ValidateSelection(SelectedManifests pair, RecordSyncPlan plan, string root)
    {
        FileScanner.Validate(pair.Other, root);
        SyncScope.Validate(pair.Other);
        if (!SyncScope.Preserve(pair.Other, pair.Local).Entries.SequenceEqual(pair.Other.Entries))
            throw new UserError("方案不能移除包含忽略内容的目录。", 409);
        var selected = plan.Choices.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Planner.Compare(pair.Local, pair.Other).Any(c => !selected.Contains(c.Path)))
            throw new UserError("方案不能修改未列出的路径。", 409);
    }
}

public sealed record RecordPlanExecutionRequest(string RepositoryId, string PlanId, string PeerId);
