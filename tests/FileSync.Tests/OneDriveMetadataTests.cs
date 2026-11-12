using FileSync.Core;
using FileSync.Sync;

namespace FileSync.Tests;

public static class OneDriveMetadataTests
{
    public static async Task Run(OneDriveContext context, CancellationToken token)
    {
        var path = Path.Combine(context.Target, "资料", "文件.txt");
        var created = File.GetCreationTimeUtc(path).AddYears(1);
        File.SetCreationTimeUtc(path, created);
        Directory.SetCreationTimeUtc(context.Target, created);
        var scanner = new FileScanner();
        var before = await scanner.ScanMetadata(context.Target, token, context.ReceiveProfile.IgnorePatterns);
        var plan = await context.Receiver.Engine.Preview(context.ReceiveProfile.Id, token);
        Check.True(plan.Changes.Count == 1 && plan.Changes.Single().Kind == "metadata",
            "只有创建时间不一致时，预览识别为元数据修改");
        await context.Receiver.Engine.Execute(plan.Id, token);
        Check.True(FileScanner.Equal(plan.Remote, await scanner.ScanMetadata(context.Target, token, plan.Remote.IgnorePatterns)),
            "同内容同修改时间的文件和根目录也能纠正创建时间");
        await context.Receiver.Engine.Undo(plan.Id, token);
        Check.True(FileScanner.Equal(before, await scanner.ScanMetadata(context.Target, token, before.IgnorePatterns)),
            "仅元数据修改的撤销精确恢复原始创建时间");
        var interrupted = await context.Receiver.Engine.Preview(context.ReceiveProfile.Id, token);
        var store = context.Receiver.Store;
        Directory.CreateDirectory(store.VaultPath(interrupted.Id));
        JsonFiles.Write(Path.Combine(store.VaultPath(interrupted.Id), "plan.json"), interrupted);
        JsonFiles.Write(store.RevisionPath(interrupted.Id), new Revision(interrupted.Id, interrupted.Profile.Id,
            interrupted.Profile.Name, "writing", DateTime.UtcNow, interrupted.Changes));
        var files = new TransactionFiles(store);
        files.Archive(interrupted);
        files.Install(interrupted);
        await context.Receiver.Engine.Recover(interrupted.Id, token);
        Check.True(FileScanner.Equal(before, await scanner.ScanMetadata(context.Target, token, before.IgnorePatterns)),
            "元数据写入中断后能从版本记录恢复创建及修改时间");
        await OneDriveTests.Receive(context.Receiver, context.ReceiveProfile, token);
    }
}
