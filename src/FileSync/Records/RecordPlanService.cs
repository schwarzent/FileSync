using FileSync.Core;
using FileSync.Security;

namespace FileSync.Records;

public sealed class RecordPlanService(RecordPlanStore plans, RecordService service, DeviceIdentity identity)
{
    private const int MaximumNameLength = 80;

    public RecordPlanDetails Preview(string repositoryId, RecordPlanRequest request)
    {
        var repository = service.Find(repositoryId);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > MaximumNameLength)
            throw new UserError("方案名称需要 1–80 个字符。");
        var local = service.Saved(repository, identity.Id).Current;
        var other = service.Saved(repository, request.DeviceId).Current;
        if (local.Record.Id != request.LocalRecordId || other.Record.Id != request.OtherRecordId)
            throw new UserError("文件清单已经更新，请重新比较后制定方案。", 409);
        if (local.Record.DeviceId == other.Record.DeviceId) throw new UserError("请选择另一台设备的清单。");
        if (request.Choices is null || request.Choices.Count == 0) throw new UserError("请至少选择一项处理方式。");
        var plan = new RecordSyncPlan(StateStore.NewId(), repository.RepositoryId, request.Name.Trim())
        {
            CreatedUtc = DateTime.UtcNow, Local = Side(local), Other = Side(other),
            Choices = request.Choices.Select(pair => new RecordPathChoice(pair.Key, pair.Value switch
            {
                "local" => local.Record.DeviceId, "other" => other.Record.DeviceId,
                _ => throw new UserError("方案处理方式无效。")
            })).ToArray()
        };
        var manifests = new SelectedManifests(local.Record.Manifest, other.Record.Manifest);
        plan = plan with { Choices = RecordSelection.Expand(plan, manifests) };
        return Describe(plan, manifests);
    }

    public RecordPlanDetails Save(string repositoryId, RecordPlanRequest request)
    {
        var details = Preview(repositoryId, request);
        plans.Save(service.Find(repositoryId), details.Plan);
        return details;
    }

    public IReadOnlyList<RecordSyncPlan> List(string repositoryId) => plans.List(service.Find(repositoryId));

    public RecordPlanDetails Details(string repositoryId, string id)
    {
        var repository = service.Find(repositoryId);
        var plan = plans.Read(repository, id);
        return Describe(plan, Manifests(repository, plan));
    }

    public SelectedManifests Manifests(RecordRepository repository, RecordSyncPlan plan) =>
        new(service.Snapshot(repository, plan.Local).Manifest, service.Snapshot(repository, plan.Other).Manifest);

    private static RecordPlanSide Side(RecordEnvelope envelope) =>
        new(RecordJson.Describe(envelope.Record), new(envelope.Record.Id, envelope.Hash));

    private RecordPlanDetails Describe(RecordSyncPlan plan, SelectedManifests manifests)
    {
        if (plan.Local.Device.DeviceId == plan.Other.Device.DeviceId ||
            plan.Choices.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Choices.Count)
            throw new UserError("方案存在重复路径或相同设备。");
        var paths = manifests.Local.Entries.Concat(manifests.Other.Entries).Select(e => e.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (plan.Choices.Any(c => !paths.Contains(c.Path))) throw new UserError("方案包含清单之外的路径。");
        var after = RecordSelection.Apply(plan, manifests);
        Validate(after.Local);
        Validate(after.Other);
        return new(plan) { LocalChanges = Planner.Compare(manifests.Local, after.Local),
            OtherChanges = Planner.Compare(manifests.Other, after.Other) };
    }

    private void Validate(Manifest manifest)
    {
        try { FileScanner.Validate(manifest, service.ValidationRoot); }
        catch (UserError error) { throw new UserError($"方案中的父目录或类型选择不完整，请同时选择相关目录与子项。{error.Message}", 409); }
    }
}
