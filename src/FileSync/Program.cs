using System.Diagnostics;
using FileSync.Web;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("FileSync 目前仅支持 Windows。");
var options = AppOptions.Parse(args);
await using var app = AppHost.Build(options);
await app.StartAsync();
var address = $"http://127.0.0.1:{options.UiPort}";
Console.WriteLine($"FileSync 已启动：{address}；按 Ctrl+C 退出。");
if (options.OpenBrowser) Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
await app.WaitForShutdownAsync();
