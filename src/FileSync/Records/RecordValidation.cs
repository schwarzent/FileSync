using System.Text.Json;
using FileSync.Core;

namespace FileSync.Records;

public static class RecordValidation
{
    public static void Configuration(RecordRepository repository, Settings settings, string stateRoot)
    {
        if (repository is null) throw new UserError("文件记录仓库配置无效。");
        StateStore.SafeId(repository.Id);
        StateStore.SafeId(repository.RepositoryId);
        if (string.IsNullOrWhiteSpace(repository.Name) || repository.Name.Length > RecordRepository.MaximumNameLength)
            throw new UserError("记录仓库名称需要 1–80 个字符。");
        var workspace = PathPolicy.Root(repository.WorkspacePath);
        PathPolicy.Disjoint(workspace, stateRoot);
        foreach (var item in settings.RecordRepositories.Where(r => r?.RepositoryPath is not null))
            PathPolicy.Disjoint(workspace, PathPolicy.Root(item.RepositoryPath!));
        _ = new IgnoreRules(repository.IgnorePatterns);
        if (repository.PeerId is not null && !settings.Peers.Any(p => p.Id == repository.PeerId))
            throw new UserError("记录仓库的局域网设备尚未配对。");
        if (repository.RepositoryPath is null) return;
        var folder = PathPolicy.Root(repository.RepositoryPath);
        PathPolicy.Disjoint(folder, workspace);
        PathPolicy.Disjoint(folder, stateRoot);
        FolderPaths(folder, settings);
    }

    private static void FolderPaths(string folder, Settings settings)
    {
        foreach (var item in settings.RecordRepositories) PathPolicy.Disjoint(folder, PathPolicy.Root(item.WorkspacePath));
        foreach (var item in settings.Profiles) ProfilePaths(folder, item);
        foreach (var item in settings.Shares) PathPolicy.Disjoint(folder, PathPolicy.Root(item.Path));
    }

    private static void ProfilePaths(string folder, Profile profile)
    {
        PathPolicy.Disjoint(folder, PathPolicy.Root(profile.TargetPath));
        if (profile.BackupPath is not null) PathPolicy.Disjoint(folder, PathPolicy.Root(profile.BackupPath));
        if (profile.RelayPath is not null) PathPolicy.Disjoint(folder, PathPolicy.Root(profile.RelayPath));
    }

    public static void Package(RecordPackage package, RecordRepository repository, string validationRoot)
    {
        if (package?.Current is null) throw new UserError("文件记录为空。");
        Envelope(package.Current, repository, validationRoot);
        var current = package.Current.Record;
        if ((current.Parent is null) != (package.Previous is null)) throw new UserError("文件记录缺少父记录。", 409);
        if (package.Previous is not null)
        {
            Envelope(package.Previous, repository, validationRoot);
            var parent = package.Previous.Record;
            if (current.Parent!.Id != parent.Id || current.Parent.Hash != package.Previous.Hash ||
                current.DeviceId != parent.DeviceId || current.Id == parent.Id)
                throw new UserError("文件记录的父引用不一致。", 409);
        }
        var before = package.Previous?.Record.Manifest ?? new Manifest(current.Manifest.RootModifiedTicks, []);
        var expected = Planner.Compare(before, current.Manifest);
        if (!expected.SequenceEqual(current.Changes)) throw new UserError("文件记录的修改列表与清单不一致。", 409);
    }

    public static void Envelope(RecordEnvelope envelope, RecordRepository repository, string validationRoot)
    {
        var record = envelope?.Record ?? throw new UserError("文件记录内容为空。");
        if (!RecordJson.ValidHash(envelope!.Hash) || envelope.Hash != RecordJson.Hash(record))
            throw new UserError("文件记录 SHA-256 校验失败。", 409);
        if (record.Format != WorkspaceRecord.FormatName || record.Version != WorkspaceRecord.CurrentVersion || record.RepositoryId != repository.RepositoryId)
            throw new UserError("文件记录格式、版本或仓库编号不匹配。");
        StateStore.SafeId(record.Id);
        Identity(record);
        FileScanner.Validate(record.Manifest, validationRoot);
        SyncScope.Validate(record.Manifest);
        if (record.Manifest.RootCreatedTicks is null || record.Manifest.Entries.Any(e => e.CreatedTicks is null))
            throw new UserError("文件记录缺少原始创建时间。");
    }

    private static void Identity(WorkspaceRecord record)
    {
        if (!RecordJson.ValidHash(record.DeviceId) || string.IsNullOrWhiteSpace(record.DeviceName) ||
            record.RecordedUtc.Kind != DateTimeKind.Utc || record.Changes is null)
            throw new UserError("文件记录的设备、时间或修改列表无效。");
        if (record.Parent is not null)
        {
            StateStore.SafeId(record.Parent.Id);
            if (!RecordJson.ValidHash(record.Parent.Hash)) throw new UserError("文件记录父引用哈希无效。");
        }
    }

    public static T Read<T>(string path)
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), RecordJson.Options) ?? throw new UserError("记录文件为空。"); }
        catch (JsonException error) { throw new UserError($"记录文件 JSON 无效：{error.Message}"); }
    }
}
