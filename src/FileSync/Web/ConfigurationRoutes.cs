using FileSync.Core;
using FileSync.Sync;

namespace FileSync.Web;

public static class ConfigurationRoutes
{
    private sealed record NameRequest(string Name);
    private sealed record ShareRequest(string Name, string Path)
    {
        public string? Id { get; init; }
        public bool AllowWrite { get; init; }
        public string IgnorePatterns { get; init; } = "";
    }
    private sealed record ProfileRequest(string? Id, string Name, string PeerId, string ShareId, string TargetPath, string? BackupPath, string? SourceName)
    {
        public string Mode { get; init; } = SyncModes.OneWay;
        public string IgnorePatterns { get; init; } = "";
    }

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
    }

    private static Settings AddShare(StateStore store, ShareRequest request)
    {
        var path = PathPolicy.Root(request.Path);
        if (!Directory.Exists(path)) throw new UserError("源文件夹不存在。");
        var id = request.Id is null ? StateStore.NewId() : StateStore.SafeId(request.Id);
        var existing = store.Current.Shares.FirstOrDefault(s => s.Id == id);
        if (request.Id is not null && existing is null) throw new UserError("共享不存在。", 404);
        if (existing is not null && existing.Path != path)
            throw new UserError("已发布共享的路径不可修改，请重新发布文件夹。");
        var share = new Share(id, Name(request.Name), path)
            { AllowWrite = request.AllowWrite, IgnorePatterns = request.IgnorePatterns };
        return store.Update(s => s with { Shares = s.Shares.Where(x => x.Id != id).Append(share).ToArray() });
    }

    private static Settings SaveProfile(StateStore store, ProfileRequest request)
    {
        var target = PathPolicy.Root(request.TargetPath);
        if (!Directory.Exists(target)) throw new UserError("目标文件夹不存在，请先创建或选择已有文件夹。");
        var profile = new Profile(request.Id is null ? StateStore.NewId() : StateStore.SafeId(request.Id),
            Name(request.Name), request.PeerId, StateStore.SafeId(request.ShareId), target,
            string.IsNullOrWhiteSpace(request.BackupPath) ? null : PathPolicy.Root(request.BackupPath))
        { SourceName = request.SourceName, Mode = request.Mode, IgnorePatterns = request.IgnorePatterns };
        return store.Update(s => s with { Profiles = s.Profiles.Where(x => x.Id != profile.Id).Append(profile).ToArray() });
    }

    private static string Name(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 80 ? value.Trim() :
        throw new UserError("名称需要 1–80 个字符。");

}
