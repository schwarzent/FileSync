using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using FileSync.Core;
using FileSync.Security;

namespace FileSync.Network;

public interface IRemoteFiles
{
    Task<Manifest> Manifest(Profile profile, CancellationToken token);
    Task Download(DownloadRequest request, CancellationToken token);
}
public sealed record DownloadRequest(Profile Profile, Entry Entry, string Destination, Action<long> Report);

public sealed class PeerClient(StateStore store, DeviceIdentity identity, Discovery discovery) : IRemoteFiles
{
    public HttpClient Connect(Peer peer)
    {
        var seen = discovery.Devices().FirstOrDefault(d => d.Id == peer.Id);
        return Connect(seen?.Address ?? peer.Address, seen?.Port ?? peer.Port, peer.Id);
    }

    public HttpClient Connect(string address, int port, string fingerprint)
    {
        var host = NetworkPolicy.Address(address).ToString();
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificates = new X509CertificateCollection { identity.Certificate },
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is X509Certificate2 certificate && DeviceIdentity.Valid(certificate) &&
                    string.Equals(DeviceIdentity.Fingerprint(certificate), fingerprint, StringComparison.Ordinal)
            }
        };
        return new(handler) { BaseAddress = new Uri($"https://{host}:{NetworkPolicy.Port(port)}/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    public Peer Find(string id) => store.Current.Peers.FirstOrDefault(p => p.Id == id)
        ?? throw new UserError("设备未配对或已被移除。");

    public async Task<IReadOnlyList<Share>> Shares(string id, CancellationToken token)
    {
        using var client = Connect(Find(id));
        using var response = await client.GetAsync("peer/shares", token);
        await Check(response, token);
        return await response.Content.ReadFromJsonAsync<Share[]>(token) ?? [];
    }

    public async Task<Manifest> Manifest(Profile profile, CancellationToken token)
    {
        using var client = Connect(Find(profile.PeerId));
        using var response = await client.GetAsync($"peer/manifest/{StateStore.SafeId(profile.ShareId)}", token);
        await Check(response, token);
        return await response.Content.ReadFromJsonAsync<Manifest>(token) ?? throw new UserError("远端清单为空。");
    }

    public async Task Download(DownloadRequest request, CancellationToken token)
    {
        using var client = Connect(Find(request.Profile.PeerId));
        var url = $"peer/file/{StateStore.SafeId(request.Profile.ShareId)}?path={Uri.EscapeDataString(request.Entry.Path)}" +
            $"&hash={request.Entry.Hash}&ticks={request.Entry.ModifiedTicks}";
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        await Check(response, token);
        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var destination = new FileStream(request.Destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, FileScanner.BufferSize, FileOptions.Asynchronous);
        var buffer = new byte[FileScanner.BufferSize];
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, count), token);
            request.Report(count);
        }
        await destination.FlushAsync(token);
    }

    public static async Task Check(HttpResponseMessage response, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode)
            throw new UserError($"设备请求失败（{(int)response.StatusCode}）：{await response.Content.ReadAsStringAsync(token)}", 502);
    }
}
