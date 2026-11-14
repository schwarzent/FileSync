using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace FileSync.Core;

public sealed record TaskConfigurationBackup
{
    public const string FormatName = "filesync-task-backup";
    public const int CurrentVersion = 1;
    public required string Format { get; init; }
    public required int Version { get; init; }
    public required DateTime ExportedUtc { get; init; }
    public required IReadOnlyList<Profile> Profiles { get; init; }
}

public sealed record TaskImportRequest(TaskConfigurationBackup Backup, string ConfigurationHash);
public sealed record TaskImportPreview(int Added, int Updated, int Unchanged)
{
    public required string ConfigurationHash { get; init; }
    public required IReadOnlyList<Profile> Profiles { get; init; }
}

public static class TaskBackupJson
{
    public static readonly JsonSerializerOptions Options = new(JsonFiles.Options)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    public static async Task<T> Read<T>(Stream stream, CancellationToken token)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, Options, token)
                ?? throw new UserError("配置备份不能为空。");
        }
        catch (JsonException error) { throw new UserError($"配置备份 JSON 格式无效：{error.Message}"); }
    }
}

public sealed class TaskBackupService(StateStore store)
{
    public TaskConfigurationBackup Export() => new()
    {
        Format = TaskConfigurationBackup.FormatName, Version = TaskConfigurationBackup.CurrentVersion,
        ExportedUtc = DateTime.UtcNow, Profiles = store.Current.Profiles
    };

    public TaskImportPreview Preview(TaskConfigurationBackup backup)
    {
        var current = store.Current;
        var next = Merge(current, backup);
        store.Validate(next);
        return Describe(current, next, backup);
    }

    public TaskImportPreview Import(TaskImportRequest request)
    {
        var before = store.Current;
        var next = store.Update(current => Hash(current) == request.ConfigurationHash
            ? Merge(current, request.Backup) : throw new UserError("配置自预览后已改变，请重新选择备份文件并预览。", 409));
        return Describe(before, next, request.Backup);
    }

    private static string Hash(Settings value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonFiles.Options)));

    private static Settings Merge(Settings current, TaskConfigurationBackup backup)
    {
        Validate(backup);
        var incoming = backup.Profiles.Select(p => Normalize(p, current)).ToDictionary(p => p.Id, StringComparer.Ordinal);
        var known = current.Profiles.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        return current with { Profiles = current.Profiles.Select(p => incoming.GetValueOrDefault(p.Id, p))
            .Concat(incoming.Values.Where(p => !known.Contains(p.Id))).ToArray() };
    }

    private static void Validate(TaskConfigurationBackup? backup)
    {
        if (backup is null || backup.Format != TaskConfigurationBackup.FormatName)
            throw new UserError("所选文件不是 FileSync 同步任务配置备份。");
        if (backup.Version != TaskConfigurationBackup.CurrentVersion) throw new UserError("配置备份版本不受支持。");
        if (backup.ExportedUtc.Kind != DateTimeKind.Utc) throw new UserError("配置备份导出时间无效。");
        if (backup.Profiles is null || backup.Profiles.Any(p => p is null)) throw new UserError("配置备份中的任务清单无效。");
        if (backup.Profiles.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != backup.Profiles.Count)
            throw new UserError("配置备份包含重复任务编号。");
    }

    private static Profile Normalize(Profile profile, Settings current)
    {
        StateStore.SafeId(profile.Id);
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80) throw new UserError("任务名称需要 1–80 个字符。");
        if (profile.PeerId is null || profile.ShareId is null) throw new UserError("任务设备或共享编号缺失。");
        if (profile.Transport == SyncTransports.Lan)
        {
            StateStore.SafeId(profile.ShareId);
            if (!current.Peers.Any(p => p.Id == profile.PeerId))
                throw new UserError($"任务「{profile.Name}」的设备尚未配对，请先在局域网设备页面配对原设备，再导入备份。");
        }
        return profile with
        {
            TargetPath = PathPolicy.Root(profile.TargetPath),
            BackupPath = profile.BackupPath is null ? null : PathPolicy.Root(profile.BackupPath),
            RelayPath = profile.RelayPath is null ? null : PathPolicy.Root(profile.RelayPath)
        };
    }

    private static TaskImportPreview Describe(Settings before, Settings after, TaskConfigurationBackup backup)
    {
        var previous = before.Profiles.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var incoming = backup.Profiles.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var restored = after.Profiles.Where(p => incoming.Contains(p.Id)).ToArray();
        var added = restored.Count(p => !previous.ContainsKey(p.Id));
        var unchanged = restored.Count(p => previous.TryGetValue(p.Id, out var old) && old == p);
        return new(added, restored.Length - added - unchanged, unchanged)
            { ConfigurationHash = Hash(before), Profiles = restored };
    }
}
