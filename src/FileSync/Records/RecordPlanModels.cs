using FileSync.Core;

namespace FileSync.Records;

public sealed record RecordPlanRequest(string Name, string DeviceId)
{
    public required string LocalRecordId { get; init; }
    public required string OtherRecordId { get; init; }
    public required IReadOnlyDictionary<string, string> Choices { get; init; }
}
public sealed record RecordPlanSide(RecordDevice Device, RecordReference Reference);
public sealed record RecordPathChoice(string Path, string SourceDeviceId);
public sealed record RecordSyncPlan(string Id, string RepositoryId, string Name)
{
    public const string FormatName = "filesync-record-plan";
    public const int CurrentVersion = 1;
    public string Format { get; init; } = FormatName;
    public int Version { get; init; } = CurrentVersion;
    public required DateTime CreatedUtc { get; init; }
    public required RecordPlanSide Local { get; init; }
    public required RecordPlanSide Other { get; init; }
    public required IReadOnlyList<RecordPathChoice> Choices { get; init; }
}
public sealed record RecordPlanDetails(RecordSyncPlan Plan)
{
    public required IReadOnlyList<Change> LocalChanges { get; init; }
    public required IReadOnlyList<Change> OtherChanges { get; init; }
}
public sealed record RecordPlanConnection(string PeerId);
public sealed record RecordShareConnection(string DeviceId, string ShareId);
public sealed record SelectedManifests(Manifest Local, Manifest Other);
