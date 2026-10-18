using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FileSync.Core;

namespace FileSync.Network;

public static class NetworkPolicy
{
    public const int DefaultUiPort = 5378;
    public const int DefaultPeerPort = 5379;
    public const int DiscoveryPort = 5380;
    public const int MaxPort = 65535;

    public static IPAddress Address(string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw new UserError("设备地址需要是局域网 IPv4 地址。");
        if (!IsLocal(ip))
            throw new UserError("当前仅允许局域网地址。");
        return ip;
    }

    private static bool IsLocal(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 127 || (bytes[0] == 192 && bytes[1] == 168) ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 169 && bytes[1] == 254);
    }

    public static int Port(int port) => port is > 0 and <= MaxPort ? port : throw new UserError("端口无效。");

    public static string[] LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && IsLocal(a.Address))
        .Select(a => a.Address.ToString()).ToArray();
}
