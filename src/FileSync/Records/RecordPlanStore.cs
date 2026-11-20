using FileSync.Core;

namespace FileSync.Records;

public sealed class RecordPlanStore(RecordStore records)
{
    public RecordSyncPlan Save(RecordRepository repository, RecordSyncPlan plan)
    {
        var root = PathPolicy.Resolve(records.Root(repository), "plans");
        Directory.CreateDirectory(root);
        RecordStore.WriteImmutable(PathPolicy.Resolve(root, StateStore.SafeId(plan.Id) + ".json"), plan);
        return plan;
    }

    public RecordSyncPlan Read(RecordRepository repository, string id)
    {
        var path = PathPolicy.Resolve(records.Root(repository), $"plans/{StateStore.SafeId(id)}.json");
        if (!File.Exists(path)) throw new UserError("同步方案不存在或尚未下载。", 404);
        var plan = RecordValidation.Read<RecordSyncPlan>(path);
        if (plan.Id != id || plan.RepositoryId != repository.RepositoryId || plan.Format != RecordSyncPlan.FormatName ||
            plan.Version != RecordSyncPlan.CurrentVersion || string.IsNullOrWhiteSpace(plan.Name) ||
            plan.CreatedUtc.Kind != DateTimeKind.Utc || plan.Choices is null || plan.Choices.Count == 0)
            throw new UserError("同步方案格式或仓库编号无效。");
        return plan;
    }

    public IReadOnlyList<RecordSyncPlan> List(RecordRepository repository)
    {
        var root = PathPolicy.Resolve(records.Root(repository), "plans");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*.json").Select(path => Read(repository, Path.GetFileNameWithoutExtension(path)))
            .OrderByDescending(p => p.CreatedUtc).ToArray();
    }
}
