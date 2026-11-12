using System.Text;
using System.Text.RegularExpressions;

namespace FileSync.Core;

public sealed class IgnoreRules
{
    private sealed record Rule(Regex Pattern, bool DirectoryOnly);
    private readonly Rule[] rules;

    public IgnoreRules(string patterns)
    {
        if (patterns is null) throw new UserError("忽略规则不能为空值。");
        rules = patterns.Split('\n').Select(Parse).OfType<Rule>().ToArray();
    }

    public static string Combine(string shared, string task) =>
        string.Join('\n', new[] { shared, task }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public bool Excludes(string path, bool directory)
    {
        return rules.Any(rule => (!rule.DirectoryOnly || directory) && rule.Pattern.IsMatch(path));
    }

    public bool ExcludesPath(Entry entry)
    {
        if (Excludes(entry.Path, entry.Directory)) return true;
        return Ancestors(entry.Path).Any(parent => Excludes(parent, true));
    }

    public static IEnumerable<string> Ancestors(string path)
    {
        for (var separator = path.LastIndexOf('/'); separator >= 0; separator = path.LastIndexOf('/'))
        {
            path = path[..separator];
            yield return path;
        }
    }

    private static Rule? Parse(string line)
    {
        var pattern = line.Trim();
        if (pattern.Length == 0 || pattern.StartsWith('#')) return null;
        if (pattern.StartsWith('!') || pattern.Contains('[') || pattern.Contains(']'))
            throw new UserError("忽略规则支持 *、**、? 和 # 注释，暂不支持 ! 反向规则或 [] 字符组。");
        var directory = pattern.EndsWith('/');
        pattern = pattern.TrimEnd('/');
        var anchored = pattern.StartsWith('/') || pattern.Contains('/');
        pattern = pattern.TrimStart('/');
        if (!ValidPattern(pattern))
            throw new UserError($"忽略规则无效，请使用相对路径和 / 分隔符：{line}");
        return new(new Regex((anchored ? "^" : "(?:^|/)") + Glob(pattern) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking), directory);
    }

    private static bool ValidPattern(string pattern) => pattern.Length > 0 && !pattern.Contains('\\') &&
        !pattern.Contains(':') && !pattern.Split('/').Any(p => p is "" or "." or "..") && !pattern.Any(char.IsControl);

    private static string Glob(string pattern)
    {
        var result = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var value = pattern[i];
            if (value == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                var recursive = Recursive(pattern, i);
                result.Append(recursive.Pattern);
                i = recursive.LastIndex;
            }
            else result.Append(value switch { '*' => "[^/]*", '?' => "[^/]", _ => Regex.Escape(value.ToString()) });
        }
        return result.ToString();
    }

    private static (string Pattern, int LastIndex) Recursive(string pattern, int index)
    {
        var slash = index + 2 < pattern.Length && pattern[index + 2] == '/';
        return (slash ? "(?:.*/)?" : ".*", index + (slash ? 2 : 1));
    }
}
