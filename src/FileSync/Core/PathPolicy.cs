namespace FileSync.Core;

public static class PathPolicy
{
    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '|', '?', '*', '\\'];
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6",
        "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string Root(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new UserError("请填写完整的本机文件夹路径。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith(@"\\?", StringComparison.Ordinal))
            throw new UserError("仅支持本机磁盘路径。");
        if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
            throw new UserError("不能将磁盘根目录设为同步文件夹。");
        AssertNoLinks(full);
        return full;
    }

    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Length > 32000)
            throw new UserError("文件相对路径无效。");
        foreach (var part in relative.Split('/'))
        {
            if (part is "" or "." or ".." || part.IndexOfAny(InvalidChars) >= 0 ||
                part.Any(char.IsControl) || part.EndsWith('.') || part.EndsWith(' ') ||
                Reserved.Contains(part.Split('.')[0]))
                throw new UserError($"不安全的文件路径：{relative}");
        }
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!Within(full, root)) throw new UserError("文件路径越出同步文件夹。");
        AssertNoLinks(full);
        return full;
    }

    public static bool Within(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static void Disjoint(string first, string second)
    {
        if (Within(first, second) || Within(second, first))
            throw new UserError("源文件夹、目标文件夹、备份位置及程序数据目录不能相互包含。");
    }

    public static void AssertNoLinks(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if ((current.Exists || File.Exists(current.FullName)) &&
                (File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new UserError($"不支持符号链接或目录联接：{current.FullName}");
        }
    }
}
