namespace FileSync.Core;

public static class SyncScope
{
    public static Manifest Filter(Manifest manifest, string patterns)
    {
        var rules = new IgnoreRules(patterns);
        return manifest with { IgnorePatterns = patterns,
            Entries = manifest.Entries.Where(e => !rules.ExcludesPath(e)).ToArray() };
    }

    public static Manifest Preserve(Manifest desired, Manifest before)
    {
        var protectedPaths = before.ExcludedPaths.SelectMany(IgnoreRules.Ancestors)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = desired.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var directory in before.Entries.Where(e => e.Directory && protectedPaths.Contains(e.Path)))
        {
            if (!entries.TryGetValue(directory.Path, out var current)) entries.Add(directory.Path, directory);
            else if (!current.Directory || current.Path != directory.Path)
                throw new UserError($"目录含有忽略内容，不能替换其类型或名称：{directory.Path}", 409);
        }
        return desired with { Entries = entries.Values.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray() };
    }

    public static void Validate(Manifest manifest)
    {
        var rules = new IgnoreRules(manifest.IgnorePatterns);
        if (manifest.Entries.Any(rules.ExcludesPath)) throw new UserError("文件清单包含已忽略的路径。", 409);
    }
}
