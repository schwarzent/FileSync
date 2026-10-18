using FileSync.Core;
using FileSync.Sync;

namespace FileSync.Tests;

public static class SyncTests
{
    public static async Task Run(TestNode local, TestNode remote, string root, CancellationToken token)
    {
        var source = Path.Combine(root, "源文件 [中文]");
        var target = Path.Combine(root, "目标文件");
        var backup = Path.Combine(root, "完整备份");
        Populate(source, target);
        var share = new Share(StateStore.NewId(), "工作资料", source);
        remote.Store.Update(s => s with { Shares = [share] });
        var profile = new Profile(StateStore.NewId(), "测试同步", remote.Identity.Id, share.Id, target, backup);
        local.Store.Update(s => s with { Profiles = [profile] });
        var scanner = new FileScanner();
        var before = await scanner.Scan(target, token);
        var originalSource = await scanner.Scan(source, token);
        var shares = await local.Client.Shares(remote.Identity.Id, token);
        Check.True(shares.Count == 1 && shares[0].Path == "", "远端共享仅暴露已发布文件夹，不泄露绝对路径");
        var plan = await local.Engine.Preview(profile.Id, token);
        Check.True(plan.Changes.Any(c => c.Kind == "add") && plan.Changes.Any(c => c.Kind == "update") && plan.Changes.Any(c => c.Kind == "delete"), "预览包含新增、更新及删除");
        Check.True(FileScanner.Equal(before, await scanner.Scan(target, token)), "预览不修改本机目标");
        var revision = await local.Engine.Execute(plan.Id, token);
        Check.True(revision.Status == "completed", "通过 HTTPS 完成真实文件同步");
        Check.True(FileScanner.Equal(originalSource, await scanner.Scan(target, token)), "内容、文件及目录 UTC 时间完整一致");
        Check.True(FileScanner.Equal(originalSource, await scanner.Scan(source, token)), "远端源文件夹保持原样");
        Check.True(FileScanner.Equal(before, await scanner.Scan(revision.BackupPath!, token)), "可选完整备份保留原始内容与时间");
        Check.True(new StateStore(local.Store.Root).History().Single().Status == "completed", "版本历史持久化可重新读取");
        await Check.Reject(() => local.Engine.Execute(plan.Id, token), "预览不可重复执行");
        await local.Engine.Undo(plan.Id, token);
        Check.True(FileScanner.Equal(before, await scanner.Scan(target, token)), "撤销恢复被删除和覆盖的文件及原始目录时间");
        await Drift(local, profile, source, target, token);
        await TypeChanges(local, profile, source, target, token);
        await CaseChanges(local, profile, source, target, token);
        await Recovery(local, profile, token);
        await InstalledRecovery(local, profile, token);
    }

    private static void Populate(string source, string target)
    {
        Directory.CreateDirectory(Path.Combine(source, "中文 [目录]", "空目录"));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "中文 [目录]", "新增.txt"), "中文 UTF-8 内容\n");
        File.WriteAllText(Path.Combine(source, "更新.txt"), "远端新内容");
        File.WriteAllText(Path.Combine(target, "更新.txt"), "本机旧内容");
        File.WriteAllText(Path.Combine(target, "删除.txt"), "保留在版本库");
        using (var file = new FileStream(Path.Combine(source, "大文件.bin"), FileMode.CreateNew))
        {
            const int fileSize = 16 * 1024 * 1024;
            file.SetLength(fileSize);
        }
        var time = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1234);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(file, time);
        foreach (var folder in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length)) Directory.SetLastWriteTimeUtc(folder, time);
        Directory.SetLastWriteTimeUtc(source, time);
    }

    private static async Task Drift(TestNode node, Profile profile, string source, string target, CancellationToken token)
    {
        var scanner = new FileScanner();
        var before = await scanner.Scan(target, token);
        var plan = await node.Engine.Preview(profile.Id, token);
        File.WriteAllText(Path.Combine(source, "更新.txt"), "预览后修改的远端文件");
        await Check.Reject(() => node.Engine.Execute(plan.Id, token), "远端预览后变化会阻止执行");
        Check.True(FileScanner.Equal(before, await scanner.Scan(target, token)), "预览失效不覆盖目标");
        var next = await node.Engine.Preview(profile.Id, token);
        File.WriteAllText(Path.Combine(target, "更新.txt"), "预览后修改的本机文件");
        await Check.Reject(() => node.Engine.Execute(next.Id, token), "本机预览后变化会阻止执行");
    }

    private static async Task TypeChanges(TestNode node, Profile profile, string source, string target, CancellationToken token)
    {
        Directory.CreateDirectory(Path.Combine(target, "变为文件"));
        File.WriteAllText(Path.Combine(target, "变为文件", "child.txt"), "旧子文件");
        File.WriteAllText(Path.Combine(source, "变为文件"), "现在是文件");
        File.WriteAllText(Path.Combine(target, "变为目录"), "旧文件");
        Directory.CreateDirectory(Path.Combine(source, "变为目录"));
        File.WriteAllText(Path.Combine(source, "变为目录", "child.txt"), "新子文件");
        var scanner = new FileScanner();
        var before = await scanner.Scan(target, token);
        var plan = await node.Engine.Preview(profile.Id, token);
        await node.Engine.Execute(plan.Id, token);
        Check.True(FileScanner.Equal(await scanner.Scan(source, token), await scanner.Scan(target, token)), "支持文件与目录相互替换，包含非空子目录");
        File.WriteAllText(Path.Combine(target, "新增本机修改.txt"), "不要覆盖");
        await Check.Reject(() => node.Engine.Undo(plan.Id, token), "撤销拒绝覆盖同步后的本机修改");
        File.Move(Path.Combine(target, "新增本机修改.txt"), Path.Combine(Path.GetDirectoryName(target)!, "保留的本机修改.txt"));
        TransactionFiles.RestoreTimes(target, plan.Remote);
        await node.Engine.Undo(plan.Id, token);
        Check.True(FileScanner.Equal(before, await scanner.Scan(target, token)), "类型替换后可完整撤销");
    }

    private static async Task Recovery(TestNode node, Profile profile, CancellationToken token)
    {
        var plan = await node.Engine.Preview(profile.Id, token);
        var vault = node.Store.VaultPath(plan.Id);
        Directory.CreateDirectory(vault);
        JsonFiles.Write(Path.Combine(vault, "plan.json"), plan);
        JsonFiles.Write(node.Store.RevisionPath(plan.Id), new Revision(plan.Id, profile.Id, profile.Name, "writing", DateTime.UtcNow, plan.Changes));
        var files = new TransactionFiles(node.Store);
        var deleted = plan.Changes.First(c => c.Kind == "delete" && c.Before is { Directory: false });
        var duplicatePath = PathPolicy.Resolve(profile.TargetPath, deleted.Path);
        var duplicateContent = File.ReadAllBytes(duplicatePath);
        files.Archive(plan);
        File.WriteAllBytes(duplicatePath, duplicateContent);
        await Check.Reject(() => node.Engine.Preview(profile.Id, token), "中断事务需先恢复，禁止继续同步");
        await node.Engine.Recover(plan.Id, token);
        Check.True(FileScanner.Equal(plan.Local, await new FileScanner().Scan(profile.TargetPath, token)), "归档后中断、原文件与归档重复时仍可恢复");
    }

    private static async Task CaseChanges(TestNode node, Profile profile, string source, string target, CancellationToken token)
    {
        Directory.CreateDirectory(Path.Combine(source, "FolderCase"));
        Directory.CreateDirectory(Path.Combine(target, "foldercase"));
        File.WriteAllText(Path.Combine(source, "FolderCase", "Item.TXT"), "同样内容");
        File.WriteAllText(Path.Combine(target, "foldercase", "item.txt"), "同样内容");
        var scanner = new FileScanner();
        var before = await scanner.Scan(target, token);
        var plan = await node.Engine.Preview(profile.Id, token);
        await node.Engine.Execute(plan.Id, token);
        Check.True(FileScanner.Equal(await scanner.Scan(source, token), await scanner.Scan(target, token)), "正确同步仅大小写变化的文件与目录名称");
        await node.Engine.Undo(plan.Id, token);
        Check.True(FileScanner.Equal(before, await scanner.Scan(target, token)), "大小写变化后可以完整撤销");
    }

    private static async Task InstalledRecovery(TestNode node, Profile profile, CancellationToken token)
    {
        var plan = await node.Engine.Preview(profile.Id, token);
        var files = new TransactionFiles(node.Store);
        foreach (var change in plan.Changes.Where(c => c.After is { Directory: false } && c.Kind != "metadata"))
            await node.Client.Download(new(profile, change.After!, files.Stage(plan, change.After!), _ => { }), token);
        JsonFiles.Write(Path.Combine(node.Store.VaultPath(plan.Id), "plan.json"), plan);
        JsonFiles.Write(node.Store.RevisionPath(plan.Id), new Revision(plan.Id, profile.Id, profile.Name, "writing", DateTime.UtcNow, plan.Changes));
        files.Archive(plan);
        files.Install(plan);
        files.Rollback(plan);
        await node.Engine.Recover(plan.Id, token);
        Check.True(FileScanner.Equal(plan.Local, await new FileScanner().Scan(profile.TargetPath, token)), "文件安装及撤销后再次中断，重复恢复保持正确");
    }
}
