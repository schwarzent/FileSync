using FileSync.Core;
using FileSync.Records;
using FileSync.Security;
using FileSync.Sync;

namespace FileSync.Web;

public static class RecordRoutes
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<RecordStore>();
        services.AddSingleton<RecordEnvironment>();
        services.AddSingleton<RecordService>();
        RecordPlanRoutes.Register(services);
    }

    public static void Map(WebApplication app)
    {
        RecordPlanRoutes.Map(app);
        app.MapPost("/api/record-repositories", (RecordRepositoryRequest request, RecordService records, SyncEngine engine) =>
            engine.Exclusive(() => Task.FromResult(records.Create(request))));
        app.MapDelete("/api/record-repositories/{id}", (string id, StateStore store, SyncEngine engine) =>
            engine.Exclusive(() => Task.FromResult(store.Update(s => s with
                { RecordRepositories = s.RecordRepositories.Where(r => r.Id != id).ToArray() }))));
        app.MapGet("/api/record-repositories/peers/{id}", (string id, RecordService records, CancellationToken token) =>
            records.RemoteRepositories(id, token));
        app.MapGet("/api/record-repositories/{id}", (string id, RecordService records, SyncEngine engine) =>
            engine.Exclusive(() => Task.FromResult(records.Devices(id))));
        app.MapPost("/api/record-repositories/{id}/generate", (string id, RecordService records, SyncEngine engine, CancellationToken token) =>
            engine.Exclusive(() => records.Generate(id, token)));
        app.MapPost("/api/record-repositories/{id}/exchange", (string id, RecordExchangeRequest request,
            RecordService records, SyncEngine engine, CancellationToken token) =>
            engine.Exclusive(() => records.Exchange(id, request, token)));
        app.MapGet("/api/record-repositories/{id}/compare", (string id, string deviceId, RecordService records, SyncEngine engine) =>
            engine.Exclusive(() => Task.FromResult(records.Compare(id, deviceId))));
        app.MapGet("/peer/record-repositories", (StateStore store) =>
            store.Current.RecordRepositories.Select(r => new { r.RepositoryId, r.Name }));
        app.MapGet("/peer/record-repositories/{id}", (string id, RecordService records, DeviceIdentity identity, SyncEngine engine) =>
            engine.Exclusive(() => Task.FromResult(records.Saved(records.FindShared(id), identity.Id))));
        app.MapPost("/peer/record-repositories/{id}", (string id, RecordPackage package, HttpContext context,
            RecordService records, SyncEngine engine) => engine.Exclusive(() =>
        {
            records.Receive(records.FindShared(id), package, context.Request.Headers["X-FileSync-Fingerprint"].ToString());
            return Task.FromResult(new { message = "文件记录已接收。" });
        }));
        app.MapGet("/records.js", () => Results.Text(LocalRoutes.Resource("records.js"), "text/javascript; charset=utf-8"));
        app.MapGet("/record-form.js", () => Results.Text(LocalRoutes.Resource("record-form.js"), "text/javascript; charset=utf-8"));
    }
}
