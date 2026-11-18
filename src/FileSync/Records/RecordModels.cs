using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using FileSync.Core;

namespace FileSync.Records;

public sealed record RecordRepository(string Id, string RepositoryId, string Name)
{
    public const int MaximumNameLength = 80;
    public required string WorkspacePath { get; init; }
    public string? RepositoryPath { get; init; }
    public string? PeerId { get; init; }
    public string IgnorePatterns { get; init; } = "";
}
public sealed record RecordRepositoryRequest(string Name, string WorkspacePath)
{
    public string? RepositoryId { get; init; }
    public string? RepositoryPath { get; init; }
    public string? PeerId { get; init; }
    public string IgnorePatterns { get; init; } = "";
}
public sealed record RecordReference(string Id, string Hash);
public sealed record RecordExchangeRequest(string PeerId);
public sealed record WorkspaceRecord(string RepositoryId, string Id, string DeviceId)
{
    public const string FormatName = "filesync-workspace-record";
    public const int CurrentVersion = 1;
    public string Format { get; init; } = FormatName;
    public int Version { get; init; } = CurrentVersion;
    public required string DeviceName { get; init; }
    public required DateTime RecordedUtc { get; init; }
    public required Manifest Manifest { get; init; }
    public RecordReference? Parent { get; init; }
    public required IReadOnlyList<Change> Changes { get; init; }
}
public sealed record RecordEnvelope(string Hash, WorkspaceRecord Record);
public sealed record RecordPackage(RecordEnvelope Current, RecordEnvelope? Previous);
public sealed record RecordDevice(string DeviceId, string DeviceName, string RecordId)
{
    public required DateTime RecordedUtc { get; init; }
    public required int Files { get; init; }
    public required long Bytes { get; init; }
    public required int Changes { get; init; }
}
public sealed record RecordDifference(string Path, string Status)
{
    public Entry? Local { get; init; }
    public Entry? Other { get; init; }
    public string? LocalChange { get; init; }
    public string? OtherChange { get; init; }
}
public sealed record RecordComparison(RecordDevice Local, RecordDevice Other, int Equal)
{
    public required IReadOnlyList<RecordDifference> Differences { get; init; }
    public bool DifferentRules { get; init; }
}

public static class RecordJson
{
    private const int HashHexLength = 64;
    public static readonly JsonSerializerOptions Options = new(JsonFiles.Options)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static string Hash(WorkspaceRecord record) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(record, Options)));
    public static bool ValidHash(string? hash) => hash?.Length == HashHexLength && hash.All(char.IsAsciiHexDigit);
    public static RecordDevice Describe(WorkspaceRecord record) => new(record.DeviceId, record.DeviceName,
        record.Id) { RecordedUtc = record.RecordedUtc, Files = record.Manifest.Entries.Count(e => !e.Directory),
            Bytes = record.Manifest.Entries.Where(e => !e.Directory).Sum(e => e.Length), Changes = record.Changes.Count };
}
