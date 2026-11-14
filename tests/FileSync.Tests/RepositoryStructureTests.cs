using FileSync.Core;
using FileSync.OneDrive;

namespace FileSync.Tests;

public static class RepositoryStructureTests
{
    public static async Task Run(OneDriveContext context, CancellationToken token)
    {
        var originals = context.Source + "-结构原始材料";
        Directory.CreateDirectory(originals);
        RepositoryTests.Write(Path.Combine(context.Source, "文件变目录"), "原来的文件");
        Directory.CreateDirectory(Path.Combine(context.Source, "目录变文件"));
        RepositoryTests.Write(Path.Combine(context.Source, "目录变文件", "旧子项.txt"), "旧子项");
        RepositoryTests.Write(Path.Combine(context.Source, "case.txt"), "大小写名称");
        await OneDriveTests.Publish(context.Sender, context.SendProfile, token);
        OneDriveTests.CopyRelay(context.SendProfile, context.ReceiveProfile);
        await OneDriveTests.Receive(context.Receiver, context.ReceiveProfile, token);
        File.Move(Path.Combine(context.Source, "文件变目录"), Path.Combine(originals, "旧文件"));
        Directory.CreateDirectory(Path.Combine(context.Source, "文件变目录"));
        RepositoryTests.Write(Path.Combine(context.Source, "文件变目录", "新子项.txt"), "新的子项");
        Directory.Move(Path.Combine(context.Source, "目录变文件"), Path.Combine(originals, "旧目录"));
        RepositoryTests.Write(Path.Combine(context.Source, "目录变文件"), "现在是文件");
        File.Move(Path.Combine(context.Source, "case.txt"), Path.Combine(originals, "case.txt"));
        File.Move(Path.Combine(originals, "case.txt"), Path.Combine(context.Source, "CASE.txt"));
        var plan = await context.Sender.Engine.Preview(context.SendProfile.Id, token);
        Check.True(plan.Conflicts.Count == 3, "OneDrive 仓库显式报告文件目录互换及仅大小写变更冲突");
        var resolved = await context.Sender.Engine.Resolve(plan.Id, plan.Conflicts.ToDictionary(p => p, _ => ConflictResolver.Local), token);
        await context.Sender.Engine.Execute(resolved.Id, token);
        await OneDriveTests.Publish(context.Sender, context.SendProfile, token);
        var root = context.SendProfile.RelayPath!;
        Check.True(File.ReadAllText(Path.Combine(root, "文件变目录", "新子项.txt")) == "新的子项" &&
            File.ReadAllText(Path.Combine(root, "目录变文件")) == "现在是文件" &&
            Directory.GetFiles(root).Select(Path.GetFileName).Contains("CASE.txt"),
            "确认冲突后，普通仓库目录正确应用两种类型替换及大小写名称");
        var changes = RepositoryHistory.Read(context.SendProfile)[0].Changes;
        Check.True(changes.Any(c => c.Kind == "delete" && c.Path == "目录变文件/旧子项.txt") &&
            changes.Any(c => c.Kind == "replace" && c.Path == "目录变文件"),
            "结构替换的历史准确记录删除的子项及类型变化");
        OneDriveTests.CopyRelay(context.SendProfile, context.ReceiveProfile);
        var incoming = await context.Receiver.Engine.Preview(context.ReceiveProfile.Id, token);
        var accepted = await context.Receiver.Engine.Resolve(incoming.Id,
            incoming.Conflicts.ToDictionary(p => p, _ => ConflictResolver.Remote), token);
        await context.Receiver.Engine.Execute(accepted.Id, token);
        Check.True(File.ReadAllText(Path.Combine(context.Target, "文件变目录", "新子项.txt")) == "新的子项" &&
            File.ReadAllText(Path.Combine(context.Target, "目录变文件")) == "现在是文件" &&
            Directory.GetFiles(context.Target).Select(Path.GetFileName).Contains("CASE.txt"),
            "另一端拉取并选择仓库版本后取得相同的结构和文件名称");
    }
}
