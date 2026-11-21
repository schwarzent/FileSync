namespace FileSync.Core;

public sealed class FileScanner
{
    public const int BufferSize = 128 * 1024;
    private const int HashHexLength = 64;

    public async Task<Manifest> Scan(string root, CancellationToken token, string ignorePatterns = "")
        => await ScanCore(root, token, new(ignorePatterns, false));

    public Task<Manifest> ScanMetadata(string root, CancellationToken token, string ignorePatterns = "")
        => ScanCore(root, token, new(ignorePatterns, true));

    private sealed record ScanOptions(string IgnorePatterns, bool CreationTime);

    private async Task<Manifest> ScanCore(string root, CancellationToken token, ScanOptions options)
    {
        PathPolicy.AssertNoLinks(root);
        if (!Directory.Exists(root)) throw new UserError($"文件夹不存在：{root}");
        var entries = new List<Entry>();
        var excluded = new List<string>();
        var rules = new IgnoreRules(options.IgnorePatterns);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (rules.Excludes(relative, Directory.Exists(path))) { excluded.Add(relative); continue; }
                await ReadEntry(new ScanContext(root, entries, pending)
                    { Path = path, CreationTime = options.CreationTime }, token);
            }
        }
        return new(Directory.GetLastWriteTimeUtc(root).Ticks,
            entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray())
            { IgnorePatterns = options.IgnorePatterns, ExcludedPaths = excluded,
                RootCreatedTicks = options.CreationTime ? Directory.GetCreationTimeUtc(root).Ticks : null };
    }

    private sealed record ScanContext(string Root, List<Entry> Entries, Stack<string> Pending)
    {
        public required string Path { get; init; }
        public bool CreationTime { get; init; }
    }

    private static async Task ReadEntry(ScanContext context, CancellationToken token)
    {
        var relative = Path.GetRelativePath(context.Root, context.Path).Replace('\\', '/');
        var path = PathPolicy.Resolve(context.Root, relative);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            context.Entries.Add(new(relative, true, 0, Directory.GetLastWriteTimeUtc(path).Ticks, null)
                { CreatedTicks = context.CreationTime ? Directory.GetCreationTimeUtc(path).Ticks : null });
            context.Pending.Push(path);
            return;
        }
        context.Entries.Add(await ScanFileReader.Read(new(path, relative, context.CreationTime), token));
    }

    public static FileStream OpenRead(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan, BufferSize = BufferSize
    });

    public static bool Equal(Manifest first, Manifest second) =>
        first.IgnorePatterns == second.IgnorePatterns && first.RootModifiedTicks == second.RootModifiedTicks &&
        first.RootCreatedTicks == second.RootCreatedTicks &&
        first.Entries.SequenceEqual(second.Entries);

    public static void Validate(Manifest? manifest, string target)
    {
        if (manifest?.Entries is null) throw new UserError("远端文件清单为空。");
        _ = new IgnoreRules(manifest.IgnorePatterns);
        if (manifest.ExcludedPaths is null || manifest.ExcludedPaths.Any(string.IsNullOrEmpty))
            throw new UserError("远端忽略路径清单无效。");
        if (!ValidTicks(manifest.RootModifiedTicks))
            throw new UserError("远端根目录时间无效。");
        if (manifest.RootCreatedTicks is long created && !ValidTicks(created))
            throw new UserError("远端根目录创建时间无效。");
        var paths = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries)
        {
            ValidateEntry(entry, target);
            if (!paths.TryAdd(entry.Path, entry)) throw new UserError("远端文件清单存在重名路径。");
        }
        foreach (var entry in manifest.Entries) ValidateParent(entry, paths);
    }

    private static void ValidateEntry(Entry entry, string target)
    {
        if (entry is null) throw new UserError("远端文件清单包含空条目。");
        PathPolicy.Resolve(target, entry.Path);
        if (entry.Length < 0 || !ValidTicks(entry.ModifiedTicks)) throw new UserError("远端文件清单的大小或时间无效。");
        if (entry.CreatedTicks is long created && !ValidTicks(created)) throw new UserError("远端创建时间无效。");
        var contentValid = entry.Directory ? entry.Length == 0 && entry.Hash is null : ValidHash(entry.Hash);
        if (!contentValid) throw new UserError("远端文件清单的目录或文件哈希无效。");
    }

    private static bool ValidTicks(long ticks) => ticks >= 0 && ticks <= DateTime.MaxValue.Ticks;
    private static bool ValidHash(string? hash) => hash?.Length == HashHexLength && hash.All(Uri.IsHexDigit);

    private static void ValidateParent(Entry entry, IReadOnlyDictionary<string, Entry> paths)
    {
        var separator = entry.Path.LastIndexOf('/');
        if (separator < 0) return;
        var parent = entry.Path[..separator];
        if (!paths.TryGetValue(parent, out var directory) || !directory.Directory || directory.Path != parent)
            throw new UserError("远端文件清单缺少父目录或父目录大小写不一致。");
    }
}
