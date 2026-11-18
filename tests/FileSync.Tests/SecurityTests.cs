using System.Net;
using System.Net.Http.Json;
using FileSync.Core;
using FileSync.Security;

namespace FileSync.Tests;

public static class SecurityTests
{
    public static void Paths(string root)
    {
        foreach (var path in new[] { "../outside", "/absolute", "a/../../secret", "C:/secret", "file:stream", "NUL.txt", "a/CON", "x\\y", "trailing.", "../.git/config" })
            Check.Reject(() => PathPolicy.Resolve(root, path), "拒绝路径 " + path);
        Check.Reject(() => PathPolicy.Root(Path.GetPathRoot(root)!), "拒绝磁盘根目录");
        Check.Reject(() => PathPolicy.Disjoint(root, Path.Combine(root, "inside")), "拒绝相互包含的文件夹");
        Check.True(PathPolicy.Resolve(root, "中文 [资料]/测试.txt").StartsWith(root), "允许中文、空格及方括号路径");
    }

    public static async Task Http(TestNode node)
    {
        using var anonymous = new HttpClient { BaseAddress = node.Ui.BaseAddress };
        using var noSession = await anonymous.GetAsync("api/status");
        Check.True(noSession.StatusCode == HttpStatusCode.Forbidden, "管理接口拒绝缺少会话头的请求");
        using var forgedOrigin = new HttpRequestMessage(HttpMethod.Get, "api/status");
        forgedOrigin.Headers.Add("Origin", "https://evil.example");
        using var response = await node.Ui.SendAsync(forgedOrigin);
        Check.True(response.StatusCode == HttpStatusCode.Forbidden, "管理接口拒绝外部 Origin");
        using var forgedHost = new HttpRequestMessage(HttpMethod.Get, "api/status");
        forgedHost.Headers.Host = "evil.example";
        using var hostResponse = await node.Ui.SendAsync(forgedHost);
        Check.True(hostResponse.StatusCode == HttpStatusCode.Forbidden, "管理接口拒绝 DNS 重绑定 Host");
        using var peer = await node.Ui.GetAsync("peer/shares");
        Check.True(peer.StatusCode == HttpStatusCode.NotFound, "管理端口不提供远端文件接口");
        var html = await node.Ui.GetStringAsync("");
        Check.True(html.Contains("文件同步") && !html.Contains("__SESSION_TOKEN__"), "中文界面及会话令牌正确加载");
        foreach (var name in new[] { "app.js", "ui.js", "forms.js", "views.js", "preview.js", "conflicts.js", "onedrive.js", "repository.js", "backup.js", "records.js", "record-form.js", "style.css", "favicon.svg" })
        {
            using var asset = await node.Ui.GetAsync(name);
            Check.True(asset.IsSuccessStatusCode, "界面资源 " + name);
        }
    }

    public static async Task Certificates(TestNode local, TestNode remote, CancellationToken token)
    {
        using var wrong = local.Client.Connect("127.0.0.1", remote.Options.PeerPort, new string('0', 64));
        using var wrongResponse = await wrong.GetAsync("peer/shares", token);
        Check.True(wrongResponse.StatusCode == HttpStatusCode.BadGateway, "拒绝服务器证书指纹不匹配");
        using var unpaired = local.Client.Connect("127.0.0.1", remote.Options.PeerPort, remote.Identity.Id);
        using var forbidden = await unpaired.GetAsync("peer/shares", token);
        Check.True(forbidden.StatusCode == HttpStatusCode.Forbidden, "未配对客户端证书无法读取共享");
        using var admin = await unpaired.GetAsync("api/status", token);
        Check.True(admin.StatusCode == HttpStatusCode.NotFound, "局域网端口不提供本机管理接口");
        var invite = remote.Invite();
        using var badInvite = await unpaired.PostAsJsonAsync("peer/pair", new PairRequest("测试", local.Options.PeerPort, new string('0', 64)), token);
        Check.True(badInvite.StatusCode == HttpStatusCode.Forbidden, "拒绝错误的一次性邀请密钥");
        using var success = await unpaired.PostAsJsonAsync("peer/pair", new PairRequest("测试", local.Options.PeerPort, invite.Secret), token);
        Check.True(success.IsSuccessStatusCode, "正确邀请可以配对");
        using var replay = await unpaired.PostAsJsonAsync("peer/pair", new PairRequest("测试", local.Options.PeerPort, invite.Secret), token);
        Check.True(replay.StatusCode == HttpStatusCode.Forbidden, "配对邀请不可重放");
        using var reloaded = new DeviceIdentity(new StateStore(local.Store.Root));
        Check.True(reloaded.Id == local.Identity.Id, "DPAPI 身份持久化后指纹保持一致");
    }
}
