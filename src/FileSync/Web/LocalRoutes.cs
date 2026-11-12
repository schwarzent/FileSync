using System.Reflection;
using FileSync.Core;
using FileSync.Network;
using FileSync.Platform;
using FileSync.Security;
using FileSync.Sync;

namespace FileSync.Web;

public static class LocalRoutes
{
    private sealed record InvitationRequest(string Address);
    private sealed record JoinRequest(string Code);
    private sealed record FolderRequest(string? Path);
    private sealed record ResolutionRequest(IReadOnlyDictionary<string, string> Choices);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/status", (StateStore store, DeviceIdentity identity, Discovery discovery, SyncEngine engine, AppOptions options) =>
            new { settings = store.Current, deviceId = identity.Id, addresses = NetworkPolicy.LocalAddresses(),
                port = options.PeerPort, discovered = discovery.Devices(), discoveryEnabled = discovery.Enabled,
                discoveryError = discovery.Error, progress = engine.Progress, busy = engine.Busy, dataPath = store.Root });
        app.MapPost("/api/invite", (InvitationRequest request, Pairing pairing, AppOptions options) =>
            pairing.Create(request.Address, options.PeerPort));
        app.MapPost("/api/pair", (JoinRequest request, Pairing pairing, AppOptions options, SyncEngine engine, CancellationToken token) =>
            engine.Exclusive(() => pairing.Join(request.Code, options.PeerPort, token)));
        app.MapGet("/api/peers/{id}/shares", (string id, PeerClient client, CancellationToken token) => client.Shares(id, token));
        app.MapPost("/api/profiles/{id}/preview", (string id, SyncEngine engine, CancellationToken token) => engine.Preview(id, token));
        app.MapPost("/api/plans/{id}/execute", (string id, SyncEngine engine, CancellationToken token) => engine.Execute(id, token));
        app.MapPost("/api/plans/{id}/resolve", (string id, ResolutionRequest request, SyncEngine engine, CancellationToken token) =>
            engine.Resolve(id, request.Choices, token));
        app.MapPost("/api/folders/pick", async (FolderRequest request, IFolderPicker picker) => new { path = await picker.Pick(request.Path) });
        app.MapGet("/api/history", (StateStore store) => store.History());
        app.MapPost("/api/cancel", (SyncEngine engine) => engine.Cancel());
        app.MapPost("/api/history/{id}/undo", (string id, SyncEngine engine, CancellationToken token) => engine.Undo(id, token));
        app.MapPost("/api/history/{id}/recover", (string id, SyncEngine engine, CancellationToken token) => engine.Recover(id, token));
        ConfigurationRoutes.Map(app);
        app.MapGet("/", (RequestSecurity security) => Results.Text(
            Resource("index.html").Replace("__SESSION_TOKEN__", security.SessionToken), "text/html; charset=utf-8"));
        app.MapGet("/app.js", () => Results.Text(Resource("app.js"), "text/javascript; charset=utf-8"));
        app.MapGet("/ui.js", () => Results.Text(Resource("ui.js"), "text/javascript; charset=utf-8"));
        app.MapGet("/forms.js", () => Results.Text(Resource("forms.js"), "text/javascript; charset=utf-8"));
        app.MapGet("/preview.js", () => Results.Text(Resource("preview.js"), "text/javascript; charset=utf-8"));
        app.MapGet("/conflicts.js", () => Results.Text(Resource("conflicts.js"), "text/javascript; charset=utf-8"));
        app.MapGet("/views.js", () => Results.Text(Resource("views.js"), "text/javascript; charset=utf-8"));
        app.MapGet("/style.css", () => Results.Text(Resource("style.css"), "text/css; charset=utf-8"));
        app.MapGet("/favicon.svg", () => Results.Text(Resource("favicon.svg"), "image/svg+xml"));
    }

    private static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"FileSync.wwwroot.{name}")
            ?? throw new InvalidOperationException($"界面资源缺失：{name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
