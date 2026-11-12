using System.Net.Http.Json;
using FileSync.Core;
using FileSync.OneDrive;
using FileSync.Sync;

namespace FileSync.Tests;

public sealed record OneDriveContext(TestNode Sender, TestNode Receiver, Profile SendProfile)
{
    public required Profile ReceiveProfile { get; init; }
    public string Source => SendProfile.TargetPath;
    public string Target => ReceiveProfile.TargetPath;
}

public static class OneDriveTests
{
    private static readonly DateTime Created = new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc).AddTicks(1234);
    private static readonly DateTime Modified = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(5678);

    public static async Task Run(SyncResourceContext nodes, CancellationToken token)
    {
        var context = await Initialize(nodes, token);
        await Initial(context, token);
        await OneDriveMetadataTests.Run(context, token);
        await OneDriveFaultTests.Run(context, token);
        await Latest(context, token);
        await Conflicts(context, token);
        await OneDriveFaultTests.ReceiverFilter(context, token);
        await IgnoredAndEmpty(context, token);
        OneDriveFaultTests.Metadata(context);
    }

    private static async Task<OneDriveContext> Initialize(SyncResourceContext nodes, CancellationToken token)
    {
        var source = Path.Combine(nodes.Root, "OneDrive 源 [中文]");
        var target = Path.Combine(nodes.Root, "OneDrive 目标");
        Directory.CreateDirectory(Path.Combine(source, "资料", "空目录"));
        Directory.CreateDirectory(Path.Combine(source, ".git"));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "资料", "文件.txt"), "原始内容，UTF-8 中文");
        File.WriteAllText(Path.Combine(source, "重复内容.txt"), "原始内容，UTF-8 中文");
        File.WriteAllText(Path.Combine(source, ".git", "ignored"), "忽略");
        File.WriteAllText(Path.Combine(source, "忽略.tmp"), "忽略");
        foreach (var path in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            info.CreationTimeUtc = Created;
            info.LastWriteTimeUtc = Modified;
        }
        Directory.SetCreationTimeUtc(source, Created);
        Directory.SetLastWriteTimeUtc(source, Modified);
        var sender = await SaveProfile(nodes.Local, new Profile(StateStore.NewId(), "OneDrive 发布", "", "", source, null)
            { Transport = SyncTransports.OneDrive, Mode = SyncModes.Latest,
                RelayPath = Path.Combine(nodes.Root, "云端中转 A"), IgnorePatterns = ".git/\n*.tmp" }, token);
        var receiver = await SaveProfile(nodes.Remote, sender with { Id = StateStore.NewId(), Name = "OneDrive 接收",
            TargetPath = target, RelayPath = Path.Combine(nodes.Root, "云端中转 B"),
            BackupPath = Path.Combine(nodes.Root, "OneDrive 完整备份") }, token);
        nodes.Local.Store.Validate(nodes.Local.Store.Current with { Peers = [], Profiles = [sender] });
        Check.True(true, "OneDrive 任务无需局域网设备配对");
        return new(nodes.Local, nodes.Remote, sender) { ReceiveProfile = receiver };
    }

    public static async Task<Profile> SaveProfile(TestNode node, Profile profile, CancellationToken token)
    {
        using var response = await node.Ui.PostAsJsonAsync("api/profiles", profile, token);
        Check.True(response.IsSuccessStatusCode, "管理接口保存 OneDrive 任务");
        return node.Store.Current.Profiles.Single(p => p.Id == profile.Id);
    }

    private static async Task Initial(OneDriveContext context, CancellationToken token)
    {
        var scanner = new FileScanner();
        var before = await scanner.ScanMetadata(context.Target, token, context.ReceiveProfile.IgnorePatterns);
        await Check.Reject(() => context.Receiver.Engine.Preview(context.ReceiveProfile.Id, token), "未收到发布记录时明确拒绝接收");
        using var response = await context.Sender.Ui.PostAsJsonAsync($"api/profiles/{context.SendProfile.Id}/publish", new { }, token);
        var published = await response.Content.ReadFromJsonAsync<PublishResult>(token);
        Check.True(response.IsSuccessStatusCode && published?.Files == 2, "发布真实内容与清单，忽略项不上传");
        var snapshot = OneDriveFiles.ReadSnapshot(context.SendProfile);
        Check.True(snapshot.DirectoryHashes.Count == 3 && snapshot.Manifest.RootCreatedTicks == Created.Ticks,
            "清单记录根目录、空目录的哈希与精确创建时间");
        var blobs = Directory.GetFiles(Path.Combine(context.SendProfile.RelayPath!, "objects"));
        Check.True(blobs.Length == 1, "相同内容只存储一份 SHA-256 数据");
        CopyRelay(context.SendProfile, context.ReceiveProfile);
        var plan = await context.Receiver.Engine.Preview(context.ReceiveProfile.Id, token);
        var revision = await context.Receiver.Engine.Execute(plan.Id, token);
        Check.True(revision.Status == "completed" && FileScanner.Equal(plan.Remote,
            await scanner.ScanMetadata(context.Target, token, plan.Remote.IgnorePatterns)),
            "中转时间全部改写后，文件、目录、根目录创建及修改时间仍逐 tick 一致");
        Check.True(FileScanner.Equal(snapshot.Manifest, await scanner.ScanMetadata(context.Source, token, snapshot.Manifest.IgnorePatterns)),
            "发布不改变原始资料时间");
        Check.True(FileScanner.Equal(before, await scanner.ScanMetadata(revision.BackupPath!, token, before.IgnorePatterns)),
            "接收前完整备份包含原始根目录创建和修改时间");
        await context.Receiver.Engine.Undo(plan.Id, token);
        Check.True(FileScanner.Equal(before, await scanner.ScanMetadata(context.Target, token, before.IgnorePatterns)),
            "撤销接收恢复内容、创建时间和修改时间");
        await Receive(context.Receiver, context.ReceiveProfile, token);
    }

    public static void CopyRelay(Profile source, Profile destination)
    {
        var relay = source.RelayPath!;
        foreach (var path in Directory.EnumerateFiles(relay, "*", SearchOption.AllDirectories))
        {
            var target = PathPolicy.Resolve(destination.RelayPath!, Path.GetRelativePath(relay, path).Replace('\\', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target, true);
            File.SetCreationTimeUtc(target, DateTime.UtcNow);
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
        }
    }

    public static async Task<SyncPlan> Receive(TestNode node, Profile profile, CancellationToken token)
    {
        var plan = await node.Engine.Preview(profile.Id, token);
        await node.Engine.Execute(plan.Id, token);
        return plan;
    }

    public static async Task Publish(TestNode node, Profile profile, CancellationToken token)
    {
        using var response = await node.Ui.PostAsJsonAsync($"api/profiles/{profile.Id}/publish", new { }, token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await response.Content.ReadAsStringAsync(token));
    }

    private static async Task Latest(OneDriveContext context, CancellationToken token)
    {
        var local = Path.Combine(context.Target, "资料", "文件.txt");
        File.WriteAllText(local, "接收端较新的内容");
        File.SetLastWriteTimeUtc(local, Modified.AddYears(1));
        await Publish(context.Sender, context.SendProfile, token);
        CopyRelay(context.SendProfile, context.ReceiveProfile);
        await Receive(context.Receiver, context.ReceiveProfile, token);
        Check.True(File.ReadAllText(local) == "接收端较新的内容", "使用原始修改时间保留本机较新版本");
        File.WriteAllText(Path.Combine(context.Target, "本机独有.txt"), "保留独有资料");
        await Publish(context.Receiver, context.ReceiveProfile, token);
        CopyRelay(context.ReceiveProfile, context.SendProfile);
        await Publish(context.Sender, context.SendProfile, token);
        Check.True(OneDriveFiles.ReadSnapshot(context.SendProfile).Manifest.Entries.Any(e => e.Path == "本机独有.txt"),
            "旧设备再次发布也保留中继较新版本与独有文件");
        await Receive(context.Sender, context.SendProfile, token);
        Check.True(File.ReadAllText(Path.Combine(context.Source, "资料", "文件.txt")) == "接收端较新的内容",
            "两端均可发布和接收，较新内容反向同步成功");
    }

    private static async Task Conflicts(OneDriveContext context, CancellationToken token)
    {
        var path = Path.Combine(context.Source, "资料", "文件.txt");
        var modified = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "同时间的另一个版本");
        File.SetLastWriteTimeUtc(path, modified);
        var plan = await context.Sender.Engine.Preview(context.SendProfile.Id, token);
        Check.True(plan.Conflicts.SequenceEqual(["资料/文件.txt"]), "同修改时间但哈希不同会产生冲突");
        await Check.Reject(() => context.Sender.Engine.Execute(plan.Id, token), "未选择版本不能覆盖同时间冲突");
        var resolved = await context.Sender.Engine.Resolve(plan.Id,
            new Dictionary<string, string> { ["资料/文件.txt"] = ConflictResolver.Local }, token);
        await context.Sender.Engine.Execute(resolved.Id, token);
        await Publish(context.Sender, context.SendProfile, token);
        Check.True(OneDriveFiles.ReadSnapshot(context.SendProfile).Manifest.Entries.Single(e => e.Path == "资料/文件.txt").Hash ==
            resolved.Desired.Entries.Single(e => e.Path == "资料/文件.txt").Hash, "手动确认的同时间版本可继续发布");
        CopyRelay(context.SendProfile, context.ReceiveProfile);
        var received = await context.Receiver.Engine.Preview(context.ReceiveProfile.Id, token);
        var accepted = await context.Receiver.Engine.Resolve(received.Id,
            new Dictionary<string, string> { ["资料/文件.txt"] = ConflictResolver.Remote }, token);
        await context.Receiver.Engine.Execute(accepted.Id, token);
        Check.True(File.ReadAllText(Path.Combine(context.Target, "资料", "文件.txt")) == "同时间的另一个版本",
            "接收端可以明确采用同时间远端版本");
    }

    private static async Task IgnoredAndEmpty(OneDriveContext context, CancellationToken token)
    {
        Check.True(!File.Exists(Path.Combine(context.Target, "忽略.tmp")) && !Directory.Exists(Path.Combine(context.Target, ".git")),
            "OneDrive 接收沿用忽略规则");
        var empty = Path.Combine(Path.GetDirectoryName(context.Source)!, "空资料");
        Directory.CreateDirectory(empty);
        Directory.SetCreationTimeUtc(empty, Created);
        Directory.SetLastWriteTimeUtc(empty, Modified);
        var profile = await SaveProfile(context.Sender, context.SendProfile with { Id = StateStore.NewId(),
            TargetPath = empty, RelayPath = empty + "-relay", IgnorePatterns = "" }, token);
        await Publish(context.Sender, profile, token);
        var snapshot = OneDriveFiles.ReadSnapshot(profile);
        Check.True(snapshot.Manifest.Entries.Count == 0 && snapshot.DirectoryHashes.Count == 1,
            "空资料文件夹也有完整根目录元数据和目录哈希");
    }
}
