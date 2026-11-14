using FileSync.Core;
using FileSync.OneDrive;
using FileSync.Sync;

namespace FileSync.Web;

public static class RepositoryRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/profiles/{id}/repository", (string id, StateStore store, SyncEngine engine) =>
            engine.Exclusive(() => Task.FromResult(new
            {
                interrupted = File.Exists(OneDriveFiles.Metadata(Profile(store, id), RepositoryWorkspace.PendingFile)),
                commits = RepositoryHistory.Read(Profile(store, id))
            })));
        app.MapGet("/api/profiles/{id}/repository/{commit}/changes/{index:int}",
            (string id, string commit, int index, StateStore store, SyncEngine engine, CancellationToken token) =>
                engine.Exclusive(() => RepositoryHistory.Compare(new(Profile(store, id), commit, index), token)));
        app.MapPost("/api/profiles/{id}/repository/recover", (string id, SyncEngine engine, CancellationToken token) =>
            engine.RecoverPublication(id, token));
    }

    private static Profile Profile(StateStore store, string id)
    {
        store.Validate(store.Current);
        var profile = store.Current.Profiles.FirstOrDefault(p => p.Id == id)
            ?? throw new UserError("同步任务不存在。", 404);
        if (profile.Transport != SyncTransports.OneDrive) throw new UserError("此任务没有 OneDrive 仓库。");
        return profile;
    }
}
