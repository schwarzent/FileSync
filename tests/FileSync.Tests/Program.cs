using FileSync.Tests;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
var workspace = Path.GetFullPath(args[0]);
var suite = args.Length == 1 ? "regression" : args[1];
var root = Path.Combine(workspace, ".test-runs", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Console.WriteLine($"隔离验证目录：{root}");
SecurityTests.Paths(root);
if (suite == "scanner")
{
    await ScanFileTests.Run(root, deadline.Token);
    Console.WriteLine("全部验证通过。");
    return;
}
await using var first = new TestNode(Path.Combine(root, "node-a"));
await using var second = new TestNode(Path.Combine(root, "node-b"));
await first.App.StartAsync(deadline.Token);
await second.App.StartAsync(deadline.Token);
await SecurityTests.Http(first);
await SecurityTests.Certificates(first, second, deadline.Token);
await first.PairTo(second, deadline.Token);
Check.True(first.Store.Current.Peers.Count == 1 && second.Store.Current.Peers.Count == 1, "双方持久化配对身份");
switch (suite)
{
    case "regression":
        await SyncTests.Run(first, second, root, deadline.Token);
        await TwoWayTests.Run(new(first, second, root), deadline.Token);
        await IgnoreTests.Run(new(first, second, root), deadline.Token);
        await ConflictTests.Run(new(first, second, root), deadline.Token);
        await FaultTests.Run(root, deadline.Token);
        break;
    case "cloud-records":
        await OneDriveTests.Run(new(first, second, root), deadline.Token);
        await RepositoryTests.Run(new(first, second, root), deadline.Token);
        await TaskBackupTests.Run(new(first, second, root), deadline.Token);
        await RecordTests.Run(new(first, second, root), deadline.Token);
        break;
    case "resources":
        await ResourceTests.Run(first, second.Identity.Id, deadline.Token);
        await SyncResourceTests.Run(new(first, second, root), deadline.Token);
        break;
    case "coexistence":
        await TransportCoexistenceTests.Run(new(first, second, root), deadline.Token);
        break;
    case "record-plans":
        RecordComparisonTests.Run();
        await RecordPlanTests.Run(new(first, second, root), deadline.Token);
        break;
    default: throw new ArgumentException($"未知测试组：{suite}");
}
second.Store.Update(s => s with { Peers = [] });
using var connection = first.Client.Connect(first.Store.Current.Peers.Single());
using var revoked = await connection.GetAsync("peer/shares", deadline.Token);
Check.True((int)revoked.StatusCode == 403, "撤销配对立即阻止原证书访问");
Console.WriteLine("全部验证通过。");
