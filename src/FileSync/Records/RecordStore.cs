using System.Text.Json;
using FileSync.Core;

namespace FileSync.Records;

public sealed class RecordStore(StateStore state)
{
    private sealed record RepositoryMarker(string Format, int Version, string RepositoryId);
    private const string MarkerFormat = "filesync-record-repository";
    private const int MarkerVersion = 1;
    public string ValidationRoot => Path.Combine(state.Root, "record-validation");

    public RecordRepository Initialize(RecordRepository repository)
    {
        var root = Root(repository);
        var marker = PathPolicy.Resolve(root, "repository.json");
        if (File.Exists(marker))
        {
            var existing = RecordValidation.Read<RepositoryMarker>(marker);
            if (existing.Format != MarkerFormat || existing.Version != MarkerVersion) throw new UserError("记录仓库格式不受支持。");
            StateStore.SafeId(existing.RepositoryId);
            if (repository.RepositoryPath is null && existing.RepositoryId != repository.RepositoryId)
                throw new UserError("记录仓库编号不匹配。", 409);
            return repository with { RepositoryId = existing.RepositoryId };
        }
        Directory.CreateDirectory(root);
        WriteImmutable(marker, new RepositoryMarker(MarkerFormat, MarkerVersion, repository.RepositoryId));
        return repository;
    }

    public string Root(RecordRepository repository)
    {
        var root = repository.RepositoryPath is null
            ? Path.Combine(state.Root, "records", StateStore.SafeId(repository.RepositoryId))
            : Path.Combine(PathPolicy.Root(repository.RepositoryPath), ".filesync-records");
        PathPolicy.AssertNoLinks(root);
        return root;
    }

    public RecordPackage? Read(RecordRepository repository, string deviceId)
    {
        if (!RecordJson.ValidHash(deviceId)) throw new UserError("记录设备编号无效。");
        var root = ExistingRoot(repository);
        var headPath = PathPolicy.Resolve(root, $"heads/{deviceId}.json");
        if (!File.Exists(headPath)) return null;
        var head = RecordValidation.Read<RecordReference>(headPath);
        var current = ReadEnvelope(repository, head);
        if (current.Record.DeviceId != deviceId) throw new UserError("记录设备与分支不匹配。", 409);
        var previous = current.Record.Parent is null ? null : ReadEnvelope(repository, current.Record.Parent);
        var package = new RecordPackage(current, previous);
        RecordValidation.Package(package, repository, ValidationRoot);
        return package;
    }

    public IReadOnlyList<RecordPackage> ReadAll(RecordRepository repository)
    {
        var folder = PathPolicy.Resolve(ExistingRoot(repository), "heads");
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.json").Select(path =>
            Read(repository, Path.GetFileNameWithoutExtension(path))!).ToArray();
    }

    public void Save(RecordRepository repository, RecordPackage package)
    {
        RecordValidation.Package(package, repository, ValidationRoot);
        var root = ExistingRoot(repository);
        if (package.Previous is not null) SaveEnvelope(repository, package.Previous);
        SaveEnvelope(repository, package.Current);
        var heads = PathPolicy.Resolve(root, "heads");
        Directory.CreateDirectory(heads);
        var record = package.Current.Record;
        PathPolicy.AssertNoLinks(PathPolicy.Resolve(root, $"heads/{record.DeviceId}.json.new"));
        JsonFiles.Write(PathPolicy.Resolve(root, $"heads/{record.DeviceId}.json"),
            new RecordReference(record.Id, package.Current.Hash), RecordJson.Options);
    }

    public RecordEnvelope ReadEnvelope(RecordRepository repository, RecordReference reference)
    {
        StateStore.SafeId(reference.Id);
        var path = PathPolicy.Resolve(Root(repository), $"snapshots/{reference.Id}.json");
        if (!File.Exists(path)) throw new UserError("记录仓库尚未同步完整，缺少快照文件。", 409);
        var envelope = RecordValidation.Read<RecordEnvelope>(path);
        RecordValidation.Envelope(envelope, repository, ValidationRoot);
        if (envelope.Hash != reference.Hash || envelope.Record.Id != reference.Id)
            throw new UserError("文件记录引用的哈希或编号不一致。", 409);
        return envelope;
    }

    private string ExistingRoot(RecordRepository repository)
    {
        var root = Root(repository);
        var marker = PathPolicy.Resolve(root, "repository.json");
        if (!File.Exists(marker)) throw new UserError("记录仓库信息缺失，请等待记录文件同步完整。", 409);
        var value = RecordValidation.Read<RepositoryMarker>(marker);
        if (value.Format != MarkerFormat || value.Version != MarkerVersion || value.RepositoryId != repository.RepositoryId)
            throw new UserError("记录仓库信息与本机配置不匹配。", 409);
        return root;
    }

    private void SaveEnvelope(RecordRepository repository, RecordEnvelope envelope)
    {
        var path = PathPolicy.Resolve(Root(repository), $"snapshots/{envelope.Record.Id}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteImmutable(path, envelope);
    }

    internal static void WriteImmutable<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, RecordJson.Options);
        if (File.Exists(path))
        {
            if (!File.ReadAllBytes(path).SequenceEqual(bytes)) throw new UserError("同编号记录已存在不同内容，禁止覆盖。", 409);
            return;
        }
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }
}
