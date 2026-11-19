namespace FileSync.Core;

public sealed class StateStore
{
    private readonly object gate = new();
    private Settings settings;
    public string Root { get; }
    public Settings Current { get { lock (gate) return settings; } }

    public StateStore(string root)
    {
        Root = PathPolicy.Root(root);
        Directory.CreateDirectory(Root);
        foreach (var child in new[] { "plans", "revisions", "vault" })
            Directory.CreateDirectory(Path.Combine(Root, child));
        var path = Path.Combine(Root, "settings.json");
        settings = File.Exists(path) ? JsonFiles.Read<Settings>(path) : Settings.Empty;
    }

    public Settings Update(Func<Settings, Settings> update)
    {
        lock (gate)
        {
            var next = update(settings);
            Validate(next);
            JsonFiles.Write(Path.Combine(Root, "settings.json"), next);
            settings = next;
            return next;
        }
    }

    public void Validate(Settings value)
    {
        if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 80)
            throw new UserError("设备名称需要 1–80 个字符。");
        if (value.RecordRepositories is null) throw new UserError("文件记录仓库配置为空。");
        foreach (var repository in value.RecordRepositories) Records.RecordValidation.Configuration(repository, value, Root);
        foreach (var share in value.Shares) _ = new IgnoreRules(share.IgnorePatterns);
        var roots = value.Shares.Select(s => PathPolicy.Root(s.Path))
            .Concat(value.Profiles.Select(p => PathPolicy.Root(p.TargetPath)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var root in roots) PathPolicy.Disjoint(root, Root);
        for (var i = 0; i < roots.Length; i++)
            for (var j = i + 1; j < roots.Length; j++) PathPolicy.Disjoint(roots[i], roots[j]);
        foreach (var profile in value.Profiles)
        {
            _ = new IgnoreRules(profile.IgnorePatterns);
            ValidateTransport(profile, value, roots);
            if (profile.BackupPath is null) continue;
            var backup = PathPolicy.Root(profile.BackupPath);
            PathPolicy.Disjoint(backup, Root);
            foreach (var root in roots) PathPolicy.Disjoint(root, backup);
            foreach (var relay in value.Profiles.Where(p => p.RelayPath is not null))
                PathPolicy.Disjoint(backup, PathPolicy.Root(relay.RelayPath!));
        }
    }

    private void ValidateTransport(Profile profile, Settings value, IReadOnlyList<string> roots)
    {
        if (profile.Transport == SyncTransports.OneDrive)
        {
            _ = new IgnoreRules(profile.UploadIgnorePatterns);
            _ = new IgnoreRules(profile.PullIgnorePatterns);
            if (profile.Mode != SyncModes.Latest) throw new UserError("OneDrive 任务必须按原始修改时间同步。");
            var relay = PathPolicy.Root(profile.RelayPath ?? "");
            PathPolicy.Disjoint(relay, Root);
            foreach (var root in roots) PathPolicy.Disjoint(relay, root);
            return;
        }
        if (profile.Transport != SyncTransports.Lan) throw new UserError("同步方式无效。");
        if (profile.Mode is not (SyncModes.OneWay or SyncModes.TwoWay)) throw new UserError("同步方向无效。");
        if (!value.Peers.Any(p => p.Id == profile.PeerId)) throw new UserError("配对设备不存在。");
        if (string.IsNullOrWhiteSpace(profile.ShareId)) throw new UserError("请选择远端源文件夹。");
        if (profile.RelayPath is not null) throw new UserError("局域网任务不能设置 OneDrive 中继路径。");
    }

    public string PlanPath(string id) => Path.Combine(Root, "plans", SafeId(id) + ".json");
    public string RevisionPath(string id) => Path.Combine(Root, "revisions", SafeId(id) + ".json");
    public string VaultPath(string id) => Path.Combine(Root, "vault", SafeId(id));
    public static string NewId() => Guid.NewGuid().ToString("N");
    public static string SafeId(string id) => Guid.TryParseExact(id, "N", out _) ? id :
        throw new UserError("记录编号无效。");

    public IReadOnlyList<Revision> History() => Directory.EnumerateFiles(Path.Combine(Root, "revisions"), "*.json")
        .Select(JsonFiles.Read<Revision>).OrderByDescending(x => x.CreatedUtc).ToArray();
}
