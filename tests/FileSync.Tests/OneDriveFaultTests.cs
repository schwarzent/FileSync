using FileSync.Core;
using FileSync.OneDrive;
using FileSync.Platform;
using FileSync.Sync;

namespace FileSync.Tests;

public static class OneDriveFaultTests
{
    public static async Task Run(OneDriveContext context, CancellationToken token)
    {
        var snapshot = OneDriveFiles.ReadSnapshot(context.ReceiveProfile);
        var entry = snapshot.Manifest.Entries.First(e => !e.Directory);
        var blob = OneDriveFiles.Blob(context.ReceiveProfile, entry.Hash!);
        var bytes = File.ReadAllBytes(blob);
        var before = await new FileScanner().ScanMetadata(context.Target, token, context.ReceiveProfile.IgnorePatterns);
        File.Move(blob, blob + ".in-transit");
        await Check.Reject(() => context.Receiver.Engine.Preview(context.ReceiveProfile.Id, token), "发布记录先到、内容未到时拒绝接收");
        File.Move(blob + ".in-transit", blob);
        File.WriteAllBytes(blob, bytes.Select(b => (byte)(b ^ byte.MaxValue)).ToArray());
        await Check.Reject(() => context.Receiver.Engine.Preview(context.ReceiveProfile.Id, token), "中继内容被损坏时 SHA-256 校验失败");
        File.WriteAllBytes(blob, bytes);
        Check.True(FileScanner.Equal(before, await new FileScanner().ScanMetadata(context.Target, token, before.IgnorePatterns)),
            "下载不完整或内容损坏均不修改本机资料");
        var plan = await context.Receiver.Engine.Preview(context.ReceiveProfile.Id, token);
        var localPath = Path.Combine(context.Target, "资料", "文件.txt");
        File.WriteAllText(localPath, "预览后的本机修改");
        await Check.Reject(() => context.Receiver.Engine.Execute(plan.Id, token), "OneDrive 预览后本机变化会使计划失效");
        File.WriteAllBytes(localPath, bytes);
        TransactionFiles.RestoreTimes(context.Target, before);
        var next = await context.Receiver.Engine.Preview(context.ReceiveProfile.Id, token);
        File.WriteAllBytes(blob, [0]);
        await Check.Reject(() => context.Receiver.Engine.Execute(next.Id, token), "OneDrive 预览后中继损坏会阻止执行");
        File.WriteAllBytes(blob, bytes);
        Check.True(FileScanner.Equal(before, await new FileScanner().ScanMetadata(context.Target, token, before.IgnorePatterns)),
            "预览失效时保留本机内容和原始时间");
        var canceled = new CancellationToken(true);
        await Check.Reject(() => context.Sender.Engine.Publish(context.SendProfile.Id,
            new OneDrivePublisher(context.Sender.Store, new FileScanner(), new OneDriveFiles()), canceled), "OneDrive 发布支持取消");
    }

    public static void Metadata(OneDriveContext context)
    {
        var snapshot = OneDriveFiles.ReadSnapshot(context.SendProfile);
        var manifest = snapshot.Manifest;
        Check.Reject(() => SnapshotMetadata.Validate(snapshot with { Version = 0 }, context.Source), "拒绝未知 OneDrive 清单版本");
        Check.Reject(() => SnapshotMetadata.Validate(snapshot with { Manifest = manifest with { RootCreatedTicks = null } }, context.Source),
            "拒绝缺失根目录创建时间的清单");
        Check.Reject(() => SnapshotMetadata.Validate(snapshot with { DirectoryHashes = new Dictionary<string, string>() }, context.Source),
            "目录元数据及哈希必须匹配");
        var file = manifest.Entries.First(e => !e.Directory);
        Check.Reject(() => SnapshotMetadata.Validate(snapshot with { Manifest = manifest with { Entries = [file with { Path = "../escape" }] } }, context.Source),
            "中继清单不能穿越本机资料路径");
        Check.True(CloudFiles.IsCloudTag(0x9000001A) && CloudFiles.IsCloudTag(0x9000F01A) &&
            !CloudFiles.IsCloudTag(0xA000000C) && !CloudFiles.IsCloudTag(0xA0000003),
            "仅识别 Windows Cloud 占位标记，符号链接和目录联接仍被拒绝");
        Check.Reject(() => context.Sender.Store.Update(s => s with { Profiles = [context.SendProfile with { RelayPath = context.Source }] }),
            "拒绝中继与资料文件夹重叠");
        var descriptor = PathPolicy.Resolve(context.SendProfile.RelayPath!, "current.json");
        var original = File.ReadAllBytes(descriptor);
        JsonFiles.Write(descriptor, new SnapshotHead(snapshot.Id, new string('0', 64)));
        Check.Reject(() => OneDriveFiles.ReadSnapshot(context.SendProfile), "发布记录必须匹配清单 SHA-256");
        File.WriteAllBytes(descriptor, original);
    }

    public static async Task ReceiverFilter(OneDriveContext context, CancellationToken token)
    {
        var entry = OneDriveFiles.ReadSnapshot(context.ReceiveProfile).Manifest.Entries.Single(e => e.Path == "重复内容.txt");
        var blob = OneDriveFiles.Blob(context.ReceiveProfile, entry.Hash!);
        File.Move(blob, blob + ".not-downloaded");
        try
        {
            var profile = context.ReceiveProfile with { IgnorePatterns = context.ReceiveProfile.IgnorePatterns + "\n/重复内容.txt" };
            var manifest = await new OneDriveFiles().Manifest(profile, token);
            Check.True(manifest.Entries.All(e => e.Path != entry.Path), "接收端额外忽略的内容不读取、不要求客户端下载");
        }
        finally { File.Move(blob + ".not-downloaded", blob); }
    }
}
