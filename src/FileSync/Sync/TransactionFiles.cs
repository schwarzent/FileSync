using System.Security.Cryptography;
using System.Text;
using FileSync.Core;

namespace FileSync.Sync;

public sealed class TransactionFiles(StateStore store)
{
    public string Stage(SyncPlan plan, Entry entry)
    {
        var root = Path.Combine(store.VaultPath(plan.Id), "stage");
        Directory.CreateDirectory(root);
        var path = PathPolicy.Resolve(root, entry.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private string Slot(SyncPlan plan, Change change, string area)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(change.Path)));
        var root = Path.Combine(store.VaultPath(plan.Id), area);
        Directory.CreateDirectory(root);
        return Path.Combine(root, key);
    }

    public static bool ReplacesContent(Change change) => change.Kind is "update" or "replace" or "delete";

    public void Archive(SyncPlan plan)
    {
        foreach (var change in plan.Changes.Where(c => c.Before is not null && ReplacesContent(c))
                     .OrderByDescending(c => c.Path.Length))
        {
            var source = PathPolicy.Resolve(plan.Profile.TargetPath, change.Before!.Path);
            Move(source, Slot(plan, change, "before"), change.Before.Directory);
        }
    }

    public void Install(SyncPlan plan)
    {
        var target = plan.Profile.TargetPath;
        foreach (var entry in plan.Desired.Entries.Where(e => e.Directory).OrderBy(e => e.Path.Length))
            Directory.CreateDirectory(PathPolicy.Resolve(target, entry.Path));
        foreach (var change in plan.Changes.Where(c => c.After is { Directory: false } && c.Kind != "metadata"))
        {
            var destination = PathPolicy.Resolve(target, change.After!.Path);
            File.Move(Stage(plan, change.After), destination);
        }
        RestoreTimes(target, plan.Desired);
    }

    public void Rollback(SyncPlan plan)
    {
        foreach (var change in plan.Changes.Where(c => c.After is not null && c.Kind != "metadata")
                     .OrderByDescending(c => c.Path.Length))
        {
            var archive = Slot(plan, change, "before");
            if (change.Before is not null && !Exists(archive)) continue;
            if (Exists(Slot(plan, change, "discarded"))) continue;
            var current = PathPolicy.Resolve(plan.Profile.TargetPath, change.After!.Path);
            if (Exists(current)) Move(current, Slot(plan, change, "discarded"), Directory.Exists(current));
        }
        foreach (var change in plan.Changes.Where(c => c.Before is not null && ReplacesContent(c))
                     .OrderBy(c => c.Path.Length))
        {
            var archive = Slot(plan, change, "before");
            if (!Exists(archive)) continue;
            var destination = PathPolicy.Resolve(plan.Profile.TargetPath, change.Before!.Path);
            if (OriginalPresent(destination, change.Before)) continue;
            Move(archive, destination, change.Before.Directory);
        }
        RestoreTimes(plan.Profile.TargetPath, plan.Local);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool OriginalPresent(string path, Entry original)
    {
        if (original.Directory) return Directory.Exists(path);
        if (!File.Exists(path)) return false;
        using var stream = FileScanner.OpenRead(path);
        if (stream.Length != original.Length || Convert.ToHexString(SHA256.HashData(stream)) != original.Hash)
            throw new UserError($"恢复位置包含不同内容，请检查目标与版本库：{original.Path}", 409);
        return true;
    }

    private static void Move(string source, string destination, bool directory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (directory && !string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.EnumerateFileSystemEntries(source).Any())
                throw new UserError("目录在同步期间出现额外文件，请检查中断记录。", 409);
            Directory.CreateDirectory(destination);
            Directory.Delete(source, false);
        }
        else if (directory) Directory.Move(source, destination);
        else File.Move(source, destination);
    }

    public static void RestoreTimes(string root, Manifest manifest)
    {
        foreach (var entry in manifest.Entries.Where(e => !e.Directory))
            File.SetLastWriteTimeUtc(PathPolicy.Resolve(root, entry.Path), new(entry.ModifiedTicks, DateTimeKind.Utc));
        foreach (var entry in manifest.Entries.Where(e => e.Directory).OrderByDescending(e => e.Path.Length))
            Directory.SetLastWriteTimeUtc(PathPolicy.Resolve(root, entry.Path), new(entry.ModifiedTicks, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(root, new(manifest.RootModifiedTicks, DateTimeKind.Utc));
    }

    public static async Task VerifyFile(string path, Entry entry, CancellationToken token)
    {
        await using var stream = FileScanner.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        if (stream.Length != entry.Length || hash != entry.Hash)
            throw new UserError($"文件完整性校验失败：{entry.Path}。目标文件未被覆盖。", 409);
    }

    public static async Task Backup(SyncPlan plan, string destination, CancellationToken token)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in plan.Local.Entries.Where(e => e.Directory))
            Directory.CreateDirectory(PathPolicy.Resolve(destination, directory.Path));
        foreach (var entry in plan.Local.Entries.Where(e => !e.Directory))
        {
            var path = PathPolicy.Resolve(destination, entry.Path);
            await using (var source = FileScanner.OpenRead(PathPolicy.Resolve(plan.Profile.TargetPath, entry.Path)))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                FileScanner.BufferSize, FileOptions.Asynchronous))
                await source.CopyToAsync(output, token);
            await VerifyFile(path, entry, token);
        }
        RestoreTimes(destination, plan.Local);
    }
}
