using FileSync.Core;

namespace FileSync.OneDrive;

public static class RepositoryRules
{
    public const string MetadataDirectory = ".filesync-repo";
    public const string MetadataPattern = "/" + MetadataDirectory + "/";

    public static string Upload(Profile profile) => IgnoreRules.Combine(profile.IgnorePatterns, profile.UploadIgnorePatterns);
    public static string Pull(Profile profile) => IgnoreRules.Combine(profile.IgnorePatterns, profile.PullIgnorePatterns);

    public static Manifest Normalize(Manifest manifest) => manifest with { IgnorePatterns = "", ExcludedPaths = [] };

    public static void ValidatePaths(Manifest manifest)
    {
        if (manifest.Entries.Any(e => e.Path.Split('/')[0].Equals(MetadataDirectory, StringComparison.OrdinalIgnoreCase)))
            throw new UserError($"资料中不能使用仓库记录目录名称：{MetadataDirectory}");
    }
}
