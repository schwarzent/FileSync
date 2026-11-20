using FileSync.Core;

namespace FileSync.Records;

public static class RecordSelection
{
    public static IReadOnlyList<RecordPathChoice> Expand(RecordSyncPlan plan, SelectedManifests records)
    {
        var entries = records.Local.Entries.Concat(records.Other.Entries).GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var requested = plan.Choices.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var path in requested.Keys)
            if (!entries.ContainsKey(path)) throw new UserError($"方案路径不在两端记录中：{path}");
        var choices = new Dictionary<string, RecordPathChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in entries.Keys)
        {
            var choice = new[] { path }.Concat(IgnoreRules.Ancestors(path)).Select(p => requested.GetValueOrDefault(p))
                .FirstOrDefault(c => c is not null);
            if (choice is not null) choices.Add(path, new(path, choice.SourceDeviceId));
        }
        var context = new SelectionContext(plan, Index(records.Local), Index(records.Other));
        foreach (var choice in choices.Values.ToArray())
            foreach (var parent in Parents(context, choice)) choices.TryAdd(parent.Path, parent);
        return choices.Values.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private sealed record SelectionContext(RecordSyncPlan Plan, IReadOnlyDictionary<string, Entry> Local,
        IReadOnlyDictionary<string, Entry> Other);

    private static IEnumerable<RecordPathChoice> Parents(SelectionContext context, RecordPathChoice choice)
    {
        var source = Source(context, choice.SourceDeviceId);
        if (!source.ContainsKey(choice.Path)) yield break;
        foreach (var path in IgnoreRules.Ancestors(choice.Path))
        {
            var parent = source[path];
            var left = context.Local.GetValueOrDefault(path);
            var right = context.Other.GetValueOrDefault(path);
            if (left is { Directory: true } && right is { Directory: true } && left.Path == right.Path) continue;
            yield return new(parent.Path, choice.SourceDeviceId);
        }
    }

    public static SelectedManifests Apply(RecordSyncPlan plan, SelectedManifests before, SelectedManifests? sources = null)
    {
        var context = new SelectionContext(plan, Index((sources ?? before).Local), Index((sources ?? before).Other));
        var selected = new Dictionary<string, Entry?>(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in plan.Choices)
            selected.Add(choice.Path, Source(context, choice.SourceDeviceId).GetValueOrDefault(choice.Path));
        return new(WithEntries(before.Local, selected), WithEntries(before.Other, selected));
    }

    private static IReadOnlyDictionary<string, Entry> Index(Manifest manifest) =>
        manifest.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);

    private static Manifest WithEntries(Manifest original, IReadOnlyDictionary<string, Entry?> selected) => original with
        { Entries = original.Entries.Where(e => !selected.ContainsKey(e.Path)).Concat(selected.Values.OfType<Entry>())
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray() };

    private static IReadOnlyDictionary<string, Entry> Source(SelectionContext context, string deviceId) =>
        deviceId == context.Plan.Local.Device.DeviceId ? context.Local : deviceId == context.Plan.Other.Device.DeviceId ? context.Other :
            throw new UserError("方案选择了未参与比较的设备。");

}
