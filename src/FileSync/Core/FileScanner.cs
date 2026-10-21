using System.Security.Cryptography;

namespace FileSync.Core;

public sealed class FileScanner
{
    public const int BufferSize = 128 * 1024;
    private const int HashHexLength = 64;

    public async Task<Manifest> Scan(string root, CancellationToken token)
    {
        PathPolicy.AssertNoLinks(root);
        if (!Directory.Exists(root)) throw new UserError($"文件夹不存在：{root}");
        var entries = new List<Entry>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
                await ReadEntry(new ScanContext(root, path, entries, pending), token);
        }
        return new(Directory.GetLastWriteTimeUtc(root).Ticks,
            entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private sealed record ScanContext(string Root, string Path, List<Entry> Entries, Stack<string> Pending);

    private static async Task ReadEntry(ScanContext context, CancellationToken token)
    {
        var relative = Path.GetRelativePath(context.Root, context.Path).Replace('\\', '/');
        var path = PathPolicy.Resolve(context.Root, relative);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            context.Entries.Add(new(relative, true, 0, Directory.GetLastWriteTimeUtc(path).Ticks, null));
            context.Pending.Push(path);
            return;
        }
        await using var stream = OpenRead(path);
        var modified = File.GetLastWriteTimeUtc(path).Ticks;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        context.Entries.Add(new(relative, false, stream.Length, modified, hash));
    }

    public static FileStream OpenRead(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan, BufferSize = BufferSize
    });

    public static bool Equal(Manifest first, Manifest second) =>
        first.RootModifiedTicks == second.RootModifiedTicks && first.Entries.SequenceEqual(second.Entries);

    public static void Validate(Manifest? manifest, string target)
    {
        if (manifest?.Entries is null) throw new UserError("远端文件清单为空。");
        if (!ValidTicks(manifest.RootModifiedTicks))
            throw new UserError("远端根目录时间无效。");
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
