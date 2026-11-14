using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FileSync.Core;
using FileSync.OneDrive;

namespace FileSync.Tests;

public static class RepositoryFaultTests
{
    public static async Task Run(OneDriveContext context, CancellationToken token)
    {
        await DamagedFiles(context, token);
        HistoryIntegrity(context.SendProfile);
        await Interrupted(context, token);
        await ReservedName(context, token);
    }

    private static async Task DamagedFiles(OneDriveContext context, CancellationToken token)
    {
        var snapshot = OneDriveFiles.ReadSnapshot(context.SendProfile);
        var file = snapshot.Manifest.Entries.First(e => !e.Directory && e.Path.EndsWith(".txt"));
        var path = PathPolicy.Resolve(context.SendProfile.RelayPath!, file.Path);
        var original = File.ReadAllBytes(path);
        var local = File.ReadAllBytes(PathPolicy.Resolve(context.Source, file.Path));
        File.WriteAllText(path, "OneDrive 尚未收到完整版本");
        await Check.Reject(() => context.Sender.Engine.Preview(context.SendProfile.Id, token), "普通仓库文件损坏或尚未到齐时停止拉取");
        using var rejected = await context.Sender.Ui.PostAsJsonAsync($"api/profiles/{context.SendProfile.Id}/publish", new { }, token);
        Check.True((int)rejected.StatusCode == 409, "普通仓库被外部修改后禁止继续提交覆盖");
        Check.True(File.ReadAllBytes(PathPolicy.Resolve(context.Source, file.Path)).SequenceEqual(local),
            "仓库校验失败不会修改本机资料");
        File.WriteAllBytes(path, original);
        var initial = RepositoryHistory.Read(context.SendProfile)[^1];
        var index = initial.Changes.ToList().FindIndex(c => c.After is { Directory: false } && c.Path.EndsWith(".txt"));
        var hash = initial.Changes[index].After!.Hash!;
        var historical = OneDriveFiles.Blob(context.SendProfile, hash);
        var bytes = File.ReadAllBytes(historical);
        File.WriteAllBytes(historical, [42]);
        await Check.Reject(() => RepositoryHistory.Compare(new(context.SendProfile, initial.Id, index), token),
            "查看历史内容前校验历史版本 SHA-256，损坏时明确报错");
        File.WriteAllBytes(historical, bytes);
    }

    private static void HistoryIntegrity(Profile profile)
    {
        var head = OneDriveFiles.Head(profile)!;
        var path = OneDriveFiles.Metadata(profile, "snapshots/" + head.SnapshotId + ".json");
        var original = File.ReadAllBytes(path);
        var snapshot = OneDriveFiles.ReadCommit(profile, head);
        var invalid = snapshot with { Sequence = snapshot.Sequence + 1 };
        JsonFiles.Write(path, invalid);
        var descriptor = OneDriveFiles.Metadata(profile, "current.json");
        JsonFiles.Write(descriptor, head with { Hash = SnapshotMetadata.Hash(File.ReadAllBytes(path)) });
        Check.Reject(() => RepositoryHistory.Read(profile), "仓库历史检查父提交和连续顺序，拒绝错误编号");
        File.WriteAllBytes(path, original);
        JsonFiles.Write(descriptor, head);
        var first = RepositoryHistory.Read(profile)[^1];
        var firstHead = FindHead(profile, first.Id);
        var firstPath = OneDriveFiles.Metadata(profile, "snapshots/" + first.Id + ".json");
        var firstBytes = File.ReadAllBytes(firstPath);
        File.WriteAllText(firstPath, "{}");
        Check.Reject(() => RepositoryHistory.Read(profile), "共享历史的旧提交同样校验父引用哈希");
        File.WriteAllBytes(firstPath, firstBytes);
        Check.True(OneDriveFiles.ReadCommit(profile, firstHead).Sequence == 1, "恢复原始历史材料后可以重新验证提交链");
    }

    private static SnapshotHead FindHead(Profile profile, string id)
    {
        var head = OneDriveFiles.Head(profile)!;
        while (head.SnapshotId != id) head = OneDriveFiles.ReadCommit(profile, head).Parent!;
        return head;
    }

    private static async Task Interrupted(OneDriveContext context, CancellationToken token)
    {
        var profile = context.SendProfile;
        var update = await PrepareUpdate(profile, token);
        JsonFiles.Write(OneDriveFiles.Metadata(profile, RepositoryWorkspace.PendingFile), update);
        await Check.Reject(() => context.Sender.Engine.Preview(profile.Id, token), "发布中断时不把部分写入的仓库当作可拉取版本");
        using var status = await context.Sender.Ui.GetAsync($"api/profiles/{profile.Id}/repository", token);
        var body = await status.Content.ReadFromJsonAsync<JsonElement>(token);
        Check.True(status.IsSuccessStatusCode && body.GetProperty("interrupted").GetBoolean(),
            "发布中断时仍能查看已提交历史并显示恢复入口");
        var foreign = Path.Combine(profile.RelayPath!, "外部新增.txt");
        File.WriteAllText(foreign, "保护外部文件");
        await Check.Reject(() => context.Sender.Engine.RecoverPublication(profile.Id, token),
            "恢复发布前发现清单以外的文件时明确停止");
        Check.True(File.ReadAllText(foreign) == "保护外部文件", "恢复失败保留仓库中的外部改动");
        File.Move(foreign, OneDriveFiles.Metadata(profile, "外部新增保留.txt"));
        var snapshot = OneDriveFiles.ReadCommit(profile, update.Next);
        var changed = snapshot.Changes.Single();
        var key = SnapshotMetadata.Hash(Encoding.UTF8.GetBytes(changed.Path));
        var archive = OneDriveFiles.Metadata(profile, "replaced/" + snapshot.Id + "/" + key);
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        File.Move(PathPolicy.Resolve(profile.RelayPath!, changed.Path), archive);
        using var response = await context.Sender.Ui.PostAsJsonAsync($"api/profiles/{profile.Id}/repository/recover", new { }, token);
        Check.True(response.IsSuccessStatusCode && OneDriveFiles.Head(profile) == update.Next &&
            !File.Exists(OneDriveFiles.Metadata(profile, RepositoryWorkspace.PendingFile)),
            "原文件已归档后中断，管理接口能完成普通文件和提交记录恢复");
        Check.True(File.ReadAllText(PathPolicy.Resolve(profile.RelayPath!, changed.Path)) == "恢复后的真实内容\n" &&
            RepositoryHistory.Read(profile)[0].Sequence == snapshot.Sequence,
            "恢复后真实内容、连续提交顺序及共享日志一致");
        OneDriveTests.CopyRelay(profile, context.ReceiveProfile);
        await OneDriveTests.Receive(context.Receiver, context.ReceiveProfile, token);
        Check.True(File.ReadAllText(PathPolicy.Resolve(context.Target, changed.Path)) == "恢复后的真实内容\n",
            "另一台设备可正常拉取恢复完成的仓库提交");
    }

    private static async Task<PublicationState> PrepareUpdate(Profile profile, CancellationToken token)
    {
        var head = OneDriveFiles.Head(profile)!;
        var previous = OneDriveFiles.ReadCommit(profile, head);
        var file = previous.Manifest.Entries.First(e => !e.Directory && e.Path.EndsWith(".txt"));
        var bytes = Encoding.UTF8.GetBytes("恢复后的真实内容\n");
        var entry = file with { Hash = SnapshotMetadata.Hash(bytes), Length = bytes.Length, ModifiedTicks = RepositoryTests.Modified.AddYears(2).Ticks };
        await File.WriteAllBytesAsync(OneDriveFiles.Blob(profile, entry.Hash!), bytes, token);
        var desired = previous.Manifest with { Entries = previous.Manifest.Entries.Select(e => e.Path == file.Path ? entry : e).ToArray() };
        var snapshot = new Snapshot(StateStore.NewId(), desired, SnapshotMetadata.DirectoryHashes(desired))
        {
            Parent = head, Sequence = previous.Sequence + 1, PublishedUtc = DateTime.UtcNow, Publisher = "恢复验证设备",
            Changes = Planner.Compare(previous.Manifest, desired)
        };
        var path = OneDriveFiles.Metadata(profile, "snapshots/" + snapshot.Id + ".json");
        JsonFiles.Write(path, snapshot);
        return new(head, new(snapshot.Id, SnapshotMetadata.Hash(File.ReadAllBytes(path))),
            await RepositoryWorkspace.Scan(profile, new FileScanner(), token));
    }

    private static async Task ReservedName(OneDriveContext context, CancellationToken token)
    {
        var reserved = Path.Combine(context.Source, RepositoryRules.MetadataDirectory);
        Directory.CreateDirectory(reserved);
        File.WriteAllText(Path.Combine(reserved, "用户内容.txt"), "不能覆盖仓库内部记录");
        var head = OneDriveFiles.Head(context.SendProfile);
        using var response = await context.Sender.Ui.PostAsJsonAsync($"api/profiles/{context.SendProfile.Id}/publish", new { }, token);
        Check.True((int)response.StatusCode == 400, "工作区与内部元数据重名时明确报错，保护仓库历史");
        Check.True(OneDriveFiles.Head(context.SendProfile) == head, "重名路径被拒绝后保留原仓库提交");
    }
}
