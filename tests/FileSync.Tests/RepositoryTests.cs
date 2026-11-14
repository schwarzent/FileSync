using System.Net.Http.Json;
using System.Text.Json;
using FileSync.Core;
using FileSync.OneDrive;

namespace FileSync.Tests;

public static class RepositoryTests
{
    public static readonly DateTime Created = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    public static readonly DateTime Modified = new(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    private const string Document = "资料/中文 [文件].txt";

    public static async Task Run(SyncResourceContext nodes, CancellationToken token)
    {
        var context = await Initialize(nodes, token);
        await Initial(context, token);
        await Edits(context, token);
        await DirectionRules(context, token);
        await RepositoryStructureTests.Run(context, token);
        await RepositoryFaultTests.Run(context, token);
    }

    private static async Task<OneDriveContext> Initialize(SyncResourceContext nodes, CancellationToken token)
    {
        var source = Path.Combine(nodes.Root, "仓库工作区 A");
        var target = Path.Combine(nodes.Root, "仓库工作区 B");
        var relay = Path.Combine(nodes.Root, "普通仓库 A");
        Directory.CreateDirectory(Path.Combine(source, "资料", "空文件夹"));
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(relay);
        Write(PathPolicy.Resolve(source, Document), "第一行\n原始中文内容\n");
        Write(Path.Combine(source, "不上传.private"), "仅本机");
        Write(Path.Combine(relay, "仅云端.txt"), "原有的普通文件");
        File.WriteAllBytes(Path.Combine(source, "二进制.bin"), [0, 255, 1]);
        var sender = await OneDriveTests.SaveProfile(nodes.Local, new(StateStore.NewId(), "普通文件仓库 A", "", "", source, null)
        {
            Transport = SyncTransports.OneDrive, Mode = SyncModes.Latest, RelayPath = relay,
            UploadIgnorePatterns = "*.private"
        }, token);
        var receiver = await OneDriveTests.SaveProfile(nodes.Remote, sender with
        {
            Id = StateStore.NewId(), Name = "普通文件仓库 B", TargetPath = target,
            RelayPath = Path.Combine(nodes.Root, "普通仓库 B"), UploadIgnorePatterns = "",
            PullIgnorePatterns = "/仅云端.txt"
        }, token);
        return new(nodes.Local, nodes.Remote, sender) { ReceiveProfile = receiver };
    }

    public static void Write(string path, string content)
    {
        File.WriteAllText(path, content);
        File.SetCreationTimeUtc(path, Created.AddTicks(1234));
        File.SetLastWriteTimeUtc(path, Modified.AddTicks(5678));
    }

    private static async Task Initial(OneDriveContext context, CancellationToken token)
    {
        await OneDriveTests.Publish(context.Sender, context.SendProfile, token);
        var profile = context.SendProfile;
        Check.True(File.ReadAllText(PathPolicy.Resolve(profile.RelayPath!, Document)) == "第一行\n原始中文内容\n" &&
            File.ReadAllBytes(Path.Combine(profile.RelayPath!, "二进制.bin")).SequenceEqual(new byte[] { 0, 255, 1 }),
            "OneDrive 仓库按原文件名保存可直接打开的明文和二进制文件");
        Check.True(File.ReadAllText(Path.Combine(profile.RelayPath!, "仅云端.txt")) == "原有的普通文件",
            "初始化仓库保留并记录 OneDrive 文件夹中已有的普通文件");
        Check.True(!File.Exists(Path.Combine(profile.RelayPath!, "不上传.private")), "独立上传规则只排除本机提交");
        var history = RepositoryHistory.Read(profile);
        var change = history.Single().Changes.Single(c => c.Path == Document);
        Check.True(history.Single().Sequence == 1 && history.Single().UploadedUtc.Kind == DateTimeKind.Utc &&
            change.After?.CreatedUtc == Created.AddTicks(1234) && change.After.ModifiedUtc == Modified.AddTicks(5678) &&
            SnapshotMetadata.ValidHash(change.After.Hash), "共享日志记录上传顺序、上传时间、原始创建及修改时间、SHA-256");
        var json = File.ReadAllText(OneDriveFiles.Metadata(profile, "history.json"));
        Check.True(json.Contains("中文 [文件].txt") && json.Contains("2020-01-02T03:04:05.0001234Z"),
            "仓库提供中文可读、带精确 UTC 日期的格式化 history.json");
        OneDriveTests.CopyRelay(context.SendProfile, context.ReceiveProfile);
        await OneDriveTests.Receive(context.Receiver, context.ReceiveProfile, token);
        Check.True(File.Exists(PathPolicy.Resolve(context.Target, Document)) &&
            !File.Exists(Path.Combine(context.Target, "仅云端.txt")), "独立拉取规则过滤云端文件，其他文件正常接收");
        using var response = await context.Receiver.Ui.GetAsync($"api/profiles/{context.ReceiveProfile.Id}/repository", token);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        Check.True(response.IsSuccessStatusCode && body.GetProperty("commits").GetArrayLength() == 1,
            "另一台设备通过管理接口直接查看仓库共享提交记录");
        Check.True(RepositoryHistory.Read(context.ReceiveProfile).Single().Id == history.Single().Id &&
            RepositoryHistory.Read(context.ReceiveProfile).Single().UploadedUtc == history.Single().UploadedUtc,
            "两端读取同一个云端提交编号，不依赖本机同步版本历史");
    }

    private static async Task Edits(OneDriveContext context, CancellationToken token)
    {
        var path = PathPolicy.Resolve(context.Target, Document);
        File.WriteAllText(path, "第一行\n笔记本修改后的中文内容\n新增行\n");
        File.SetLastWriteTimeUtc(path, Modified.AddYears(1));
        await OneDriveTests.Publish(context.Receiver, context.ReceiveProfile, token);
        var history = RepositoryHistory.Read(context.ReceiveProfile);
        Check.True(history.Count == 2 && history[0].Sequence == 2 && history[0].ParentId == history[1].Id,
            "笔记本可独立提交，前后提交编号和修改顺序连续");
        var index = history[0].Changes.ToList().FindIndex(c => c.Path == Document);
        var comparison = await RepositoryHistory.Compare(new(context.ReceiveProfile, history[0].Id, index), token);
        Check.True(comparison.Before?.Text == "第一行\n原始中文内容\n" &&
            comparison.After?.Text == "第一行\n笔记本修改后的中文内容\n新增行\n", "仓库历史保留并返回真实修改前后内容");
        using var response = await context.Receiver.Ui.GetAsync(
            $"api/profiles/{context.ReceiveProfile.Id}/repository/{history[0].Id}/changes/{index}", token);
        Check.True(response.IsSuccessStatusCode, "文件内容比较通过管理接口读取真实历史版本");
        var binaryIndex = history[1].Changes.ToList().FindIndex(c => c.Path == "二进制.bin");
        var binary = await RepositoryHistory.Compare(new(context.ReceiveProfile, history[1].Id, binaryIndex), token);
        Check.True(binary.After?.Kind == "binary" && binary.After.Text is null, "二进制历史明确展示类型与哈希，不生成伪造文本");
        OneDriveTests.CopyRelay(context.ReceiveProfile, context.SendProfile);
        await OneDriveTests.Receive(context.Sender, context.SendProfile, token);
        Check.True(File.ReadAllText(PathPolicy.Resolve(context.Source, Document)) == comparison.After!.Text,
            "电脑 A 拉取笔记本 B 的提交后取得修改内容");
        Check.True(!Directory.Exists(Path.Combine(context.Source, RepositoryRules.MetadataDirectory)),
            "拉取不会将仓库内部历史文件混入用户工作区");
    }

    private static async Task DirectionRules(OneDriveContext context, CancellationToken token)
    {
        var profile = await OneDriveTests.SaveProfile(context.Sender,
            context.SendProfile with { UploadIgnorePatterns = "*.private\n/仅云端.txt", PullIgnorePatterns = "/二进制.bin" }, token);
        await OneDriveTests.Publish(context.Sender, profile, token);
        Check.True(OneDriveFiles.ReadSnapshot(profile).Manifest.Entries.Any(e => e.Path == "仅云端.txt") &&
            File.Exists(Path.Combine(profile.RelayPath!, "仅云端.txt")), "上传忽略规则不会从仓库清单或普通目录移除已有文件");
        var cleanRules = await OneDriveTests.SaveProfile(context.Receiver, context.ReceiveProfile with { PullIgnorePatterns = "" }, token);
        OneDriveTests.CopyRelay(profile, cleanRules);
        await OneDriveTests.Receive(context.Receiver, cleanRules, token);
        Check.True(File.Exists(Path.Combine(context.Target, "仅云端.txt")), "移除拉取过滤后可接收先前忽略的仓库文件");
        var stored = new StateStore(context.Sender.Store.Root).Current.Profiles.Single(p => p.Id == profile.Id);
        Check.True(stored.UploadIgnorePatterns == profile.UploadIgnorePatterns && stored.PullIgnorePatterns == profile.PullIgnorePatterns,
            "上传和拉取规则经管理接口保存并可在重启后恢复");
    }
}
