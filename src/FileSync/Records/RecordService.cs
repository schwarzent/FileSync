using System.Net.Http.Json;
using FileSync.Core;
using FileSync.Network;
using FileSync.Security;

namespace FileSync.Records;

public sealed class RecordEnvironment(StateStore state, FileScanner scanner, DeviceIdentity identity)
{
    public StateStore State { get; } = state;
    public FileScanner Scanner { get; } = scanner;
    public DeviceIdentity Identity { get; } = identity;
}

public sealed class RecordService(RecordStore records, RecordEnvironment environment, PeerClient peers)
{
    private readonly StateStore state = environment.State;
    private readonly FileScanner scanner = environment.Scanner;
    private readonly DeviceIdentity identity = environment.Identity;

    public RecordRepository Create(RecordRepositoryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new UserError("请填写记录仓库名称。");
        var repository = new RecordRepository(StateStore.NewId(),
            StateStore.SafeId(request.RepositoryId ?? StateStore.NewId()).ToLowerInvariant(), request.Name.Trim())
        {
            WorkspacePath = PathPolicy.Root(request.WorkspacePath),
            RepositoryPath = string.IsNullOrWhiteSpace(request.RepositoryPath) ? null : PathPolicy.Root(request.RepositoryPath),
            PeerId = request.PeerId, IgnorePatterns = request.IgnorePatterns
        };
        ValidateNew(repository);
        repository = records.Initialize(repository);
        if (request.RepositoryId is not null && !string.Equals(request.RepositoryId, repository.RepositoryId, StringComparison.OrdinalIgnoreCase))
            throw new UserError("所选文件夹属于另一个记录仓库。");
        ValidateNew(repository);
        state.Update(s => s with { RecordRepositories = s.RecordRepositories.Append(repository).ToArray() });
        return repository;
    }

    private void ValidateNew(RecordRepository repository)
    {
        if (!Directory.Exists(repository.WorkspacePath)) throw new UserError("本机资料文件夹不存在。");
        if (state.Current.RecordRepositories.Any(r => string.Equals(r.RepositoryId, repository.RepositoryId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.WorkspacePath, repository.WorkspacePath, StringComparison.OrdinalIgnoreCase)))
            throw new UserError("本机已经加入此仓库或记录了此资料文件夹。");
        state.Validate(state.Current with { RecordRepositories = state.Current.RecordRepositories.Append(repository).ToArray() });
    }

    public RecordRepository Find(string id) => state.Current.RecordRepositories.FirstOrDefault(r => r.Id == id)
        ?? throw new UserError("记录仓库不存在。", 404);
    public RecordRepository FindShared(string id) => state.Current.RecordRepositories.FirstOrDefault(r => r.RepositoryId == id)
        ?? throw new UserError("本机尚未加入此记录仓库。", 404);

    public async Task<RecordDevice> Generate(string id, CancellationToken token)
    {
        var repository = Find(id);
        state.Validate(state.Current);
        var previous = records.Read(repository, identity.Id)?.Current;
        var manifest = await scanner.ScanMetadata(repository.WorkspacePath, token, repository.IgnorePatterns);
        FileScanner.Validate(manifest, records.ValidationRoot);
        var before = previous?.Record.Manifest ?? new Manifest(manifest.RootModifiedTicks, []);
        var record = new WorkspaceRecord(repository.RepositoryId, StateStore.NewId(), identity.Id)
        {
            DeviceName = state.Current.Name, RecordedUtc = DateTime.UtcNow, Manifest = manifest,
            Parent = previous is null ? null : new(previous.Record.Id, previous.Hash),
            Changes = Planner.Compare(before, manifest)
        };
        records.Save(repository, new(new(RecordJson.Hash(record), record), previous));
        return RecordJson.Describe(record);
    }

    public IReadOnlyList<RecordDevice> Devices(string id) => records.ReadAll(Find(id))
        .Select(p => RecordJson.Describe(p.Current.Record)).ToArray();

    public RecordComparison Compare(string id, string otherDevice)
    {
        if (otherDevice == identity.Id) throw new UserError("请选择另一台设备的记录。");
        var repository = Find(id);
        var local = Saved(repository, identity.Id).Current.Record;
        var other = Saved(repository, otherDevice).Current.Record;
        return RecordComparisonService.Compare(local, other);
    }

    public RecordPackage Saved(RecordRepository repository, string deviceId) => records.Read(repository, deviceId)
        ?? throw new UserError("尚无该设备的文件记录，请先生成记录，再通过局域网交换或等待 OneDrive 同步。", 409);

    public async Task<IReadOnlyList<object>> RemoteRepositories(string peerId, CancellationToken token)
    {
        using var client = peers.Connect(peers.Find(peerId));
        using var response = await client.GetAsync("peer/record-repositories", token);
        await PeerClient.Check(response, token);
        return await response.Content.ReadFromJsonAsync<object[]>(token) ?? throw new UserError("远端记录仓库列表为空。");
    }

    public async Task<object> Exchange(string id, RecordExchangeRequest request, CancellationToken token)
    {
        var repository = Find(id);
        var own = Saved(repository, identity.Id);
        using var client = peers.Connect(peers.Find(request.PeerId));
        var endpoint = $"peer/record-repositories/{repository.RepositoryId}";
        using var response = await client.GetAsync(endpoint, token);
        await PeerClient.Check(response, token);
        var incoming = await response.Content.ReadFromJsonAsync<RecordPackage>(token)
            ?? throw new UserError("远端文件记录为空。");
        Receive(repository, incoming, request.PeerId);
        using var sent = await client.PostAsJsonAsync(endpoint, own, token);
        await PeerClient.Check(sent, token);
        state.Update(s => s with { RecordRepositories = s.RecordRepositories.Select(r =>
            r.Id == id ? r with { PeerId = request.PeerId } : r).ToArray() });
        return new { message = "双方文件记录已交换，未传输资料文件。", devices = Devices(id) };
    }

    public void Receive(RecordRepository repository, RecordPackage package, string deviceId)
    {
        if (package?.Current?.Record?.DeviceId != deviceId) throw new UserError("文件记录的设备身份与配对证书不一致。", 403);
        records.Save(repository, package);
    }
}
