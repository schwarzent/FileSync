using FileSync.Core;
using FileSync.Network;
using FileSync.Sync;
using Microsoft.AspNetCore.Http.Features;

namespace FileSync.Web;

public static class TransactionRoutes
{
    public static void Map(WebApplication app)
    {
        const string root = "/peer/transactions/{id}";
        app.MapPost(root, async (RemotePreparation preparation, HttpContext context, IncomingTransactions incoming) =>
        {
            await incoming.Prepare(Request(context), preparation, context.RequestAborted);
            return new TransactionState("staging");
        });
        app.MapGet(root, (HttpContext context, IncomingTransactions incoming) => incoming.Status(Request(context)));
        app.MapPost(root + "/file", Upload);
        app.MapPost(root + "/commit", async (HttpContext context, IncomingTransactions incoming) =>
        {
            var revision = await incoming.Commit(Request(context), context.RequestAborted);
            return new TransactionState(revision.Status);
        });
        app.MapPost(root + "/validate-undo", (HttpContext context, IncomingTransactions incoming) =>
            incoming.ValidateUndo(Request(context), context.RequestAborted));
        app.MapPost(root + "/undo", async (HttpContext context, IncomingTransactions incoming) =>
            new TransactionState((await incoming.Undo(Request(context), context.RequestAborted)).Status));
        app.MapPost(root + "/recover", async (HttpContext context, IncomingTransactions incoming) =>
            new TransactionState((await incoming.Recover(Request(context), context.RequestAborted)).Status));
    }

    private static async Task<TransactionState> Upload(HttpContext context, IncomingTransactions incoming)
    {
        // 上传大小由事务清单逐块校验，允许流式传输大文件。
        var size = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (size is not null) size.MaxRequestBodySize = null;
        var upload = new IncomingUpload(Request(context), context.Request.Query["path"].ToString(), context.Request.Body);
        return new((await incoming.Upload(upload, context.RequestAborted)).Status);
    }

    private static IncomingRequest Request(HttpContext context) => new(
        StateStore.SafeId(context.Request.RouteValues["id"]?.ToString() ?? ""),
        context.Request.Headers["X-FileSync-Fingerprint"].ToString());
}
