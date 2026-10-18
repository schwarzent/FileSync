using System.Net;
using System.Security.Cryptography;
using FileSync.Core;
using FileSync.Network;

namespace FileSync.Web;

public sealed class RequestSecurity(AppOptions options, StateStore store, SecureTransport transport, ILogger<RequestSecurity> logger)
{
    public string SessionToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string Origin => $"http://127.0.0.1:{options.UiPort}";

    public async Task Invoke(HttpContext context, RequestDelegate next)
    {
        try
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; style-src 'self'; script-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Path.StartsWithSegments("/peer")) AuthorizePeer(context);
            else AuthorizeLocal(context);
            await next(context);
        }
        catch (Exception error) when (!context.Response.HasStarted)
        {
            var status = error switch
            {
                UserError user => user.Status,
                OperationCanceledException => 499,
                FileNotFoundException or DirectoryNotFoundException => 404,
                BadHttpRequestException bad => bad.StatusCode,
                _ => 500
            };
            if (status >= 500) logger.LogError(error, "请求处理失败");
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new { error = error.Message }, CancellationToken.None);
        }
    }

    private void AuthorizeLocal(HttpContext context)
    {
        var request = context.Request;
        if (!IPAddress.IsLoopback(context.Connection.RemoteIpAddress!) || request.Host.Value != $"127.0.0.1:{options.UiPort}")
            throw new UserError("仅允许从本机管理地址访问。", 403);
        if (request.Path.StartsWithSegments("/peer")) throw new UserError("传输接口不在管理端口提供。", 404);
        if (request.Headers.TryGetValue("Origin", out var origin) && origin != Origin)
            throw new UserError("请求来源不受信任。", 403);
        if (request.Path.StartsWithSegments("/api") && request.Headers["X-FileSync-Session"] != SessionToken)
            throw new UserError("本机会话无效，请刷新页面。", 403);
    }

    private void AuthorizePeer(HttpContext context)
    {
        if (context.Request.Headers["X-FileSync-Bridge"] != transport.Token)
            throw new UserError("传输接口需要安全通信授权。", 404);
        var fingerprint = context.Request.Headers["X-FileSync-Fingerprint"].ToString();
        if (context.Request.Path == "/peer/pair") return;
        if (!store.Current.Peers.Any(p => p.Id == fingerprint))
            throw new UserError("设备未配对或授权已撤销。", 403);
    }
}
