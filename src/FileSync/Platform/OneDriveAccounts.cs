using Microsoft.Win32;

namespace FileSync.Platform;

public sealed record OneDriveAccount(string Name, string Path);

public static class OneDriveAccounts
{
    public static IReadOnlyList<OneDriveAccount> Read()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("OneDrive 本机账号检测仅支持 Windows。");
        var accounts = new List<OneDriveAccount>();
        using var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
        foreach (var name in root?.GetSubKeyNames() ?? [])
        {
            using var account = root!.OpenSubKey(name);
            var path = account?.GetValue("UserFolder") as string;
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                accounts.Add(new(account?.GetValue("DisplayName") as string ?? name, path));
        }
        foreach (var name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var path = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) accounts.Add(new(name, path));
        }
        return accounts.DistinctBy(a => a.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
