using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FileSync.Core;
using FileSync.Security;
using FileSync.Web;

namespace FileSync.Network;

public sealed class SecureTransport(DeviceIdentity identity, StateStore store, AppOptions options,
    IHostApplicationLifetime lifetime, ILogger<SecureTransport> logger) : IHostedService, IDisposable
{
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public int LocalPort { get; private set; }
    private Process? process;
    private bool stopping;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var executable = Extract();
        process = new Process { StartInfo = new(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        }, EnableRaisingEvents = true };
        process.Exited += (_, _) =>
        {
            if (stopping) return;
            logger.LogCritical("安全通信进程已退出，应用停止，防止无通信状态继续运行。");
            lifetime.StopApplication();
        };
        process.Start();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) logger.LogError("TLS：{Message}", e.Data); };
        process.BeginErrorReadLine();
        using var key = identity.Certificate.GetECDsaPrivateKey()!;
        var config = new { Backend = $"127.0.0.1:{options.UiPort}", Port = options.PeerPort, options.LoopbackOnly, Token,
            Certificate = identity.Certificate.ExportCertificatePem(), Key = key.ExportPkcs8PrivateKeyPem() };
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(config));
        await process.StandardInput.FlushAsync(cancellationToken);
        var ready = await process.StandardOutput.ReadLineAsync(cancellationToken);
        LocalPort = int.TryParse(ready, out var port) ? NetworkPolicy.Port(port) :
            throw new UserError("安全通信进程启动失败，请查看控制台错误。");
    }

    private string Extract()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("FileSync.Transport.exe")
            ?? throw new UserError("安全通信组件缺失，请重新构建。");
        var hash = Convert.ToHexString(SHA256.HashData(resource));
        var folder = Path.Combine(store.Root, "tools");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, hash + ".exe");
        PathPolicy.AssertNoLinks(path);
        if (!File.Exists(path))
        {
            resource.Position = 0;
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            resource.CopyTo(output, FileScanner.BufferSize);
            output.Flush(true);
        }
        using var file = File.OpenRead(path);
        if (Convert.ToHexString(SHA256.HashData(file)) != hash) throw new UserError("安全通信组件完整性校验失败。");
        return path;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        stopping = true;
        if (process is null || process.HasExited) return;
        process.StandardInput.Close();
        await process.WaitForExitAsync(cancellationToken);
    }

    public void Dispose() => process?.Dispose();
}
