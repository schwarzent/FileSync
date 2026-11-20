using FileSync.Core;
using FileSync.Network;

namespace FileSync.Web;

public sealed record AppOptions(string DataPath, int UiPort, int PeerPort, bool Discovery, bool OpenBrowser)
{
    public bool LoopbackOnly { get; init; }
    public static AppOptions Parse(string[] args)
    {
        var data = Path.Combine(AppContext.BaseDirectory, ".filesync");
        var ui = NetworkPolicy.DefaultUiPort;
        var peer = NetworkPolicy.DefaultPeerPort;
        var discovery = true;
        var browser = true;
        var loopback = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data": data = Value(args, ref i); break;
                case "--ui-port": ui = NetworkPolicy.Port(int.Parse(Value(args, ref i))); break;
                case "--port": peer = NetworkPolicy.Port(int.Parse(Value(args, ref i))); break;
                case "--no-discovery": discovery = false; break;
                case "--no-browser": browser = false; break;
                case "--loopback-only": loopback = true; discovery = false; break;
                default: throw new UserError($"未知启动参数：{args[i]}");
            }
        }
        if (ui == peer) throw new UserError("管理端口和传输端口不能相同。");
        return new(PathPolicy.Root(data), ui, peer, discovery, browser) { LoopbackOnly = loopback };
    }

    private static string Value(string[] args, ref int index) => ++index < args.Length ? args[index] :
        throw new UserError("启动参数缺少值。");
}
