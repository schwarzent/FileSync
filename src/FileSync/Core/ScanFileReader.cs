using System.Security.Cryptography;

namespace FileSync.Core;

internal sealed record ScanFileRequest(string FullPath, string RelativePath, bool IncludeCreationTime);

internal static class ScanFileReader
{
    private const int WindowsErrorMask = 0xFFFF;
    private const int SharingViolation = 32;
    private const int LockViolation = 33;
    private sealed record FileState(long Length, long ModifiedTicks, long? CreatedTicks);

    public static async Task<Entry> Read(ScanFileRequest request, CancellationToken token)
    {
        try { return await ReadCore(request, token); }
        catch (IOException error) when ((error.HResult & WindowsErrorMask) is SharingViolation or LockViolation)
        {
            throw new UserError($"扫描未完成，文件被其他程序独占或锁定：{request.FullPath}。请关闭占用该文件的程序后重新扫描；未跳过此文件。", 409);
        }
    }

    private static async Task<Entry> ReadCore(ScanFileRequest request, CancellationToken token)
    {
        await using var stream = new FileStream(request.FullPath, new FileStreamOptions
        {
            Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan, BufferSize = FileScanner.BufferSize
        });
        var before = Capture(stream, request.IncludeCreationTime);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        var current = new FileInfo(request.FullPath);
        if (before != Capture(stream, request.IncludeCreationTime) || stream.Position != before.Length ||
            current.Length != before.Length || current.LastWriteTimeUtc.Ticks != before.ModifiedTicks ||
            request.IncludeCreationTime && current.CreationTimeUtc.Ticks != before.CreatedTicks)
            throw new UserError($"扫描期间文件发生变化：{request.FullPath}。请停止编辑后重新扫描。", 409);
        return new(request.RelativePath, false, before.Length, before.ModifiedTicks, hash)
            { CreatedTicks = before.CreatedTicks };
    }

    private static FileState Capture(FileStream stream, bool creationTime) =>
        new(stream.Length, File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks,
            creationTime ? File.GetCreationTimeUtc(stream.SafeFileHandle).Ticks : null);
}
