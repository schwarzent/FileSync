using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FileSync.Core;
using FileSync.Security;

namespace FileSync.Network;

public sealed class Discovery(StateStore store, DeviceIdentity identity, ILogger<Discovery> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(50);
    private readonly ConcurrentDictionary<string, DiscoveredDevice> devices = new();
    public int PeerPort { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Error { get; private set; }
    private sealed record Announcement(string Protocol, string Id, string Name, int Port);

    public IReadOnlyList<DiscoveredDevice> Devices()
    {
        foreach (var pair in devices)
            if (DateTime.UtcNow - pair.Value.SeenUtc > Lifetime) devices.TryRemove(pair.Key, out _);
        return devices.Values.OrderBy(d => d.Name).ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            using var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.ExclusiveAddressUse = false;
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, NetworkPolicy.DiscoveryPort));
            socket.EnableBroadcast = true;
            var receive = Receive(socket, cancellation.Token);
            var announce = Announce(socket, cancellation.Token);
            await Task.WhenAny(receive, announce);
            await cancellation.CancelAsync();
            await Task.WhenAll(receive, announce);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (SocketException error)
        {
            Error = $"局域网发现失败：{error.Message}";
            logger.LogError(error, "局域网设备发现失败");
        }
    }

    private async Task Announce(UdpClient socket, CancellationToken token)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            var data = JsonSerializer.SerializeToUtf8Bytes(new Announcement("filesync/1", identity.Id, store.Current.Name, PeerPort));
            await socket.SendAsync(data, new IPEndPoint(IPAddress.Broadcast, NetworkPolicy.DiscoveryPort), token);
        } while (await timer.WaitForNextTickAsync(token));
    }

    private async Task Receive(UdpClient socket, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var packet = await socket.ReceiveAsync(token);
            if (packet.Buffer.Length > 2048) continue;
            try
            {
                var device = JsonSerializer.Deserialize<Announcement>(packet.Buffer);
                if (device is null || device.Id is null || device.Name is null || device.Protocol != "filesync/1" || device.Id == identity.Id ||
                    device.Id.Length != 64 || !device.Id.All(Uri.IsHexDigit) || device.Name.Length > 80) continue;
                var address = NetworkPolicy.Address(packet.RemoteEndPoint.Address.ToString()).ToString();
                devices[device.Id] = new(device.Id, device.Name, address, NetworkPolicy.Port(device.Port), DateTime.UtcNow);
                Devices();
            }
            catch (Exception error) when (error is JsonException or UserError)
            { logger.LogDebug("忽略无效的发现报文：{Message}", error.Message); }
        }
    }
}
