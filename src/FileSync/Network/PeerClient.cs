using System.Net.Http.Json;
using FileSync.Core;

namespace FileSync.Network;

public interface IRemoteFiles
{
    Task<Manifest> Manifest(Profile profile, CancellationToken token);
    Task Download(DownloadRequest request, CancellationToken token);
}
public sealed record DownloadRequest(Profile Profile, Entry Entry, string Destination, Action<long> Report);

public sealed partial class PeerClient(StateStore store, SecureTransport transport, Discovery discovery) : IRemoteFiles
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
            UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{transport.LocalPort}/"), Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Add("X-FileSync-Bridge", transport.Token);
        client.DefaultRequestHeaders.Add("X-FileSync-Target", $"{host}:{NetworkPolicy.Port(port)}");
        client.DefaultRequestHeaders.Add("X-FileSync-Pin", fingerprint);
        return client;
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
        using var response = await client.PostAsJsonAsync($"peer/manifest/{StateStore.SafeId(profile.ShareId)}",
            new ManifestRequest(profile.IgnorePatterns) { CreationTime = profile.PreserveCreationTime }, token);
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
        long received = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            received += count;
            if (received > request.Entry.Length) throw new UserError($"远端文件超过清单声明的大小：{request.Entry.Path}", 409);
            await destination.WriteAsync(buffer.AsMemory(0, count), token);
            request.Report(count);
        }
        await destination.FlushAsync(token);
        destination.Flush(true);
        if (received != request.Entry.Length) throw new UserError($"远端文件传输不完整：{request.Entry.Path}", 409);
    }

    public static async Task Check(HttpResponseMessage response, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode)
            throw new UserError($"设备请求失败（{(int)response.StatusCode}）：{await response.Content.ReadAsStringAsync(token)}", 502);
    }
}

public sealed record ManifestRequest(string IgnorePatterns)
{
    public bool CreationTime { get; init; }
}
