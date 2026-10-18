using FileSync.Core;
using FileSync.Sync;

namespace FileSync.Web;

public static class ConfigurationRoutes
{
    private sealed record NameRequest(string Name);
    private sealed record ShareRequest(string Name, string Path);
    private sealed record ProfileRequest(string? Id, string Name, string PeerId, string ShareId, string TargetPath, string? BackupPath, string? SourceName);

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/name", (NameRequest request, StateStore store, SyncEngine engine) =>
            engine.Exclusive(() => Task.FromResult(store.Update(s => s with { Name = request.Name.Trim() }))));
        app.MapPost("/api/shares", (ShareRequest request, StateStore store, SyncEngine engine) =>
            engine.Exclusive(() => Task.FromResult(AddShare(store, request))));
        app.MapDelete("/api/shares/{id}", (string id, StateStore store, SyncEngine engine) =>
            engine.Exclusive(() => Task.FromResult(store.Update(s => s with { Shares = s.Shares.Where(x => x.Id != id).ToArray() }))));
        app.MapPost("/api/profiles", (ProfileRequest request, StateStore store, SyncEngine engine) =>
            engine.Exclusive(() => Task.FromResult(SaveProfile(store, request))));
        app.MapDelete("/api/profiles/{id}", (string id, StateStore store, SyncEngine engine) =>
            engine.Exclusive(() => Task.FromResult(store.Update(s => s with { Profiles = s.Profiles.Where(x => x.Id != id).ToArray() }))));
        app.MapDelete("/api/peers/{id}", (string id, StateStore store, SyncEngine engine) =>
            engine.Exclusive(() => Task.FromResult(store.Update(s => s with
            {
                Peers = s.Peers.Where(x => x.Id != id).ToArray(),
                Profiles = s.Profiles.Where(x => x.PeerId != id).ToArray()
            }))));
        app.MapGet("/api/folders", (string? path) => Browse(path));
    }

    private static Settings AddShare(StateStore store, ShareRequest request)
    {
        var path = PathPolicy.Root(request.Path);
        if (!Directory.Exists(path)) throw new UserError("源文件夹不存在。");
        var share = new Share(StateStore.NewId(), Name(request.Name), path);
        return store.Update(s => s with { Shares = s.Shares.Append(share).ToArray() });
    }

    private static Settings SaveProfile(StateStore store, ProfileRequest request)
    {
        var target = PathPolicy.Root(request.TargetPath);
        if (!Directory.Exists(target)) throw new UserError("目标文件夹不存在，请先创建或选择已有文件夹。");
        var profile = new Profile(request.Id is null ? StateStore.NewId() : StateStore.SafeId(request.Id),
            Name(request.Name), request.PeerId, StateStore.SafeId(request.ShareId), target,
            string.IsNullOrWhiteSpace(request.BackupPath) ? null : PathPolicy.Root(request.BackupPath))
        { SourceName = request.SourceName };
        return store.Update(s => s with { Profiles = s.Profiles.Where(x => x.Id != profile.Id).Append(profile).ToArray() });
    }

    private static string Name(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 80 ? value.Trim() :
        throw new UserError("名称需要 1–80 个字符。");

    private static object Browse(string? path)
    {
        if (string.IsNullOrEmpty(path)) return new { path = "", parent = (string?)null,
            folders = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .Select(d => new { name = d.Name, path = d.Name }).ToArray() };
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new UserError("请选择本机文件夹。");
        var full = Path.GetFullPath(path);
        PathPolicy.AssertNoLinks(full);
        return new { path = full, parent = Directory.GetParent(full)?.FullName,
            folders = Directory.EnumerateDirectories(full).Select(p => new DirectoryInfo(p))
                .Where(d => (d.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) == 0)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => new { name = d.Name, path = d.FullName }).ToArray() };
    }
}
