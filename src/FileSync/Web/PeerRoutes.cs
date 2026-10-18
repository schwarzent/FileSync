using System.Security.Cryptography;
using FileSync.Core;
using FileSync.Network;

namespace FileSync.Web;

public static class PeerRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/peer/pair", (PairRequest request, HttpContext context, Pairing pairing, AppOptions options) =>
            pairing.Accept(request, context.Request.Headers["X-FileSync-Fingerprint"].ToString(),
                context.Request.Headers["X-FileSync-Address"].ToString(), options.PeerPort));
        app.MapGet("/peer/shares", (StateStore store) =>
            store.Current.Shares.Select(s => s with { Path = "" }));
        app.MapGet("/peer/manifest/{id}", async (string id, StateStore store, FileScanner scanner, CancellationToken token) =>
            await scanner.Scan(FindShare(store, id).Path, token));
        app.MapGet("/peer/file/{id}", ServeFile);
    }

    private static Share FindShare(StateStore store, string id) =>
        store.Current.Shares.FirstOrDefault(s => s.Id == id) ?? throw new UserError("源文件夹不存在或已取消共享。", 404);

    private static async Task<IResult> ServeFile(string id, string path, string hash, long ticks,
        StateStore store, CancellationToken token)
    {
        var full = PathPolicy.Resolve(FindShare(store, id).Path, path);
        var stream = FileScanner.OpenRead(full);
        try
        {
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
            if (actualHash != hash || File.GetLastWriteTimeUtc(full).Ticks != ticks)
                throw new UserError("源文件已修改，请重新预览。", 409);
            stream.Position = 0;
            return Results.Stream(stream, "application/octet-stream", enableRangeProcessing: false);
        }
        catch { await stream.DisposeAsync(); throw; }
    }
}
