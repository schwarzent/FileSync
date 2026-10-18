using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FileSync.Core;
using FileSync.Network;
using FileSync.Security;
using FileSync.Sync;
using FileSync.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace FileSync.Tests;

public sealed class TestNode : IAsyncDisposable
{
    public WebApplication App { get; }
    public HttpClient Ui { get; }
    public StateStore Store => App.Services.GetRequiredService<StateStore>();
    public DeviceIdentity Identity => App.Services.GetRequiredService<DeviceIdentity>();
    public Pairing Pairing => App.Services.GetRequiredService<Pairing>();
    public PeerClient Client => App.Services.GetRequiredService<PeerClient>();
    public SyncEngine Engine => App.Services.GetRequiredService<SyncEngine>();
    public AppOptions Options { get; }

    public TestNode(string root)
    {
        Options = new(root, FreePort(), FreePort(), false, false);
        App = AppHost.Build(Options);
        Ui = new() { BaseAddress = new Uri($"http://127.0.0.1:{Options.UiPort}/"), Timeout = TimeSpan.FromSeconds(10) };
        Ui.DefaultRequestHeaders.Add("X-FileSync-Session", App.Services.GetRequiredService<RequestSecurity>().SessionToken);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public Invite Invite()
    {
        var value = Pairing.Create("127.0.0.1", Options.PeerPort);
        var json = JsonSerializer.SerializeToElement(value, JsonFiles.Options);
        return JsonSerializer.Deserialize<Invite>(Convert.FromBase64String(json.GetProperty("code").GetString()!), JsonFiles.Options)!;
    }

    public async Task PairTo(TestNode remote, CancellationToken token)
    {
        var invite = remote.Invite();
        var code = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(invite, JsonFiles.Options));
        await Pairing.Join(code, Options.PeerPort, token);
    }

    public async ValueTask DisposeAsync()
    {
        Ui.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
