using System.Text.Json.Serialization;

namespace FileSync.Core;

public sealed record Share(string Id, string Name, string Path)
{
    public bool AllowWrite { get; init; }
    public string IgnorePatterns { get; init; } = "";
}
public sealed record Peer(string Id, string Name, string Address, int Port);
public sealed record Profile(string Id, string Name, string PeerId, string ShareId,
    string TargetPath, string? BackupPath)
{
    public string? SourceName { get; init; }
    public string Mode { get; init; } = SyncModes.OneWay;
    public string IgnorePatterns { get; init; } = "";
    public string Transport { get; init; } = SyncTransports.Lan;
    public string? RelayPath { get; init; }
    public string UploadIgnorePatterns { get; init; } = "";
    public string PullIgnorePatterns { get; init; } = "";
}
public static class SyncModes
{
    public const string OneWay = "one-way";
    public const string TwoWay = "two-way";
    public const string Latest = "latest";
}
public static class SyncTransports
{
    public const string Lan = "lan";
    public const string OneDrive = "onedrive";
}
public sealed record Settings(string Name, IReadOnlyList<Share> Shares,
    IReadOnlyList<Peer> Peers, IReadOnlyList<Profile> Profiles)
{
    public static Settings Empty => new(Environment.MachineName, [], [], []);
}
public sealed record Entry(string Path, bool Directory, long Length, long ModifiedTicks, string? Hash)
{
    public long? CreatedTicks { get; init; }
}
public sealed record Manifest(long RootModifiedTicks, IReadOnlyList<Entry> Entries)
{
    public string IgnorePatterns { get; init; } = "";
    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];
    public long? RootCreatedTicks { get; init; }
}
public sealed record Change(string Kind, string Path, Entry? Before, Entry? After);
public sealed record SyncPlan(string Id, Profile Profile, Manifest Local, Manifest Remote,
    IReadOnlyList<Change> Changes, DateTime CreatedUtc)
{
    public Manifest? Merged { get; init; }
    public IReadOnlyList<Change> RemoteChanges { get; init; } = [];
    public IReadOnlyList<string> Conflicts { get; init; } = [];
    public string? OwnerPeerId { get; init; }
    [JsonIgnore]
    public Manifest Desired => Merged ?? Remote;
}
public sealed record Revision(string Id, string ProfileId, string ProfileName, string Status,
    DateTime CreatedUtc, IReadOnlyList<Change> Changes, string? Error = null, string? BackupPath = null)
{
    public bool LocalWriteStarted { get; init; }
    public string Mode { get; init; } = SyncModes.OneWay;
    public IReadOnlyList<Change> RemoteChanges { get; init; } = [];
}
public sealed record Invite(string Address, int Port, string Fingerprint, string Secret);
public sealed record PairRequest(string Name, int Port, string Secret);
public sealed record PairResponse(string Name, string Fingerprint, int Port);
public sealed record DiscoveredDevice(string Id, string Name, string Address, int Port, DateTime SeenUtc);
public sealed record SyncProgress(string Phase, string? Path, long Bytes, long TotalBytes);

public sealed class UserError(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}
