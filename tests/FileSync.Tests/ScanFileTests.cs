using System.Security.Cryptography;
using FileSync.Core;

namespace FileSync.Tests;

public static class ScanFileTests
{
    private const int ChangingFileLength = 16 * 1024 * 1024;

    public static async Task Run(string root, CancellationToken token)
    {
        await SharedDocument(Path.Combine(root, "共享打开文档"), token);
        await ExclusiveDocument(Path.Combine(root, "独占打开文档"), token);
        await ChangingDocument(Path.Combine(root, "扫描期间变化"), token);
    }

    private static async Task SharedDocument(string root, CancellationToken token)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "已打开文档.docx");
        await File.WriteAllTextAsync(path, "文档已打开但尚未保存新的编辑", token);
        var expectedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, token)));
        var created = File.GetCreationTimeUtc(path).Ticks;
        var modified = File.GetLastWriteTimeUtc(path).Ticks;
        await using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var manifest = await new FileScanner().ScanMetadata(root, token);
        var entry = manifest.Entries.Single();
        Check.True(entry.Path == Path.GetFileName(path) && entry.Hash == expectedHash &&
            entry.CreatedTicks == created && entry.ModifiedTicks == modified,
            "文档以可写方式打开时仍可扫描，共享读取保留完整哈希和原始时间");
    }

    private static async Task ExclusiveDocument(string root, CancellationToken token)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "独占文档.docx");
        await File.WriteAllTextAsync(path, "不能跳过的文件", token);
        await using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try { await new FileScanner().ScanMetadata(root, token); }
        catch (UserError error) when (error.Status == 409 && error.Message.Contains(path) && error.Message.Contains("未跳过"))
        {
            Check.True(true, "独占文件明确返回具体路径和关闭程序指引，不生成缺项清单");
            return;
        }
        throw new InvalidOperationException("独占文件没有明确阻止扫描。");
    }

    private static async Task ChangingDocument(string root, CancellationToken token)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "正在保存的文档.bin");
        await using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        writer.SetLength(ChangingFileLength);
        writer.Flush(true);
        var scanning = new FileScanner().ScanMetadata(root, token);
        Check.True(!scanning.IsCompleted, "变化测试已进入真实异步文件哈希读取");
        writer.SetLength(ChangingFileLength + 1);
        writer.Flush(true);
        try { await scanning; }
        catch (UserError error) when (error.Status == 409 && error.Message.Contains(path) && error.Message.Contains("发生变化"))
        {
            Check.True(true, "扫描期间写入文件会明确失败，不记录混合内容或错误大小");
            return;
        }
        throw new InvalidOperationException("扫描期间文件变化未被识别。");
    }
}
