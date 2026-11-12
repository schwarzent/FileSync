using System.Security.Cryptography;
using FileSync.Core;
using FileSync.Network;

namespace FileSync.Web;

public static class PeerRoutes
{
    private sealed class FileRequest
    {
        public required string Id { get; init; }
        public required string Path { get; init; }
        public required string Hash { get; init; }
        public required long Ticks { get; init; }
    }

    public static void Map(WebApplication app)
    {
        app.MapPost("/peer/pair", (PairRequest request, HttpContext context, Pairing pairing, AppOptions options) =>
            pairing.Accept(request, context.Request.Headers["X-FileSync-Fingerprint"].ToString(),
                context.Request.Headers["X-FileSync-Address"].ToString(), options.PeerPort));
        app.MapGet("/peer/shares", (StateStore store) =>
            store.Current.Shares.Select(s => s with { Path = "" }));
        app.MapPost("/peer/manifest/{id}", Scan);
        app.MapGet("/peer/file/{id}", ServeFile);
    }

    private static Share FindShare(StateStore store, string id) =>
        store.Current.Shares.FirstOrDefault(s => s.Id == id) ?? throw new UserError("源文件夹不存在或已取消共享。", 404);

    private static async Task<Manifest> Scan(HttpContext context, StateStore store, FileScanner scanner)
    {
        var id = StateStore.SafeId(context.Request.RouteValues["id"]?.ToString() ?? "");
        var request = await context.Request.ReadFromJsonAsync<ManifestRequest>(context.RequestAborted)
            ?? throw new UserError("请提供任务忽略规则。");
        var share = FindShare(store, id);
        _ = new IgnoreRules(request.IgnorePatterns);
        return await scanner.Scan(share.Path, context.RequestAborted, IgnoreRules.Combine(share.IgnorePatterns, request.IgnorePatterns));
    }

    private static async Task<IResult> ServeFile([AsParameters] FileRequest request, StateStore store, CancellationToken token)
    {
        var share = FindShare(store, request.Id);
        var requested = new Entry(request.Path, false, 0, request.Ticks, request.Hash);
        if (new IgnoreRules(share.IgnorePatterns).ExcludesPath(requested))
            throw new UserError("文件位于忽略范围内。", 403);
        var full = PathPolicy.Resolve(share.Path, request.Path);
        var stream = FileScanner.OpenRead(full);
        try
        {
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
            if (actualHash != request.Hash || File.GetLastWriteTimeUtc(full).Ticks != request.Ticks)
                throw new UserError("源文件已修改，请重新预览。", 409);
            stream.Position = 0;
            return Results.Stream(stream, "application/octet-stream", enableRangeProcessing: false);
        }
        catch { await stream.DisposeAsync(); throw; }
    }
}
