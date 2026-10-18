using System.Text.Json;

namespace FileSync.Core;

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Options)
        ?? throw new InvalidDataException($"配置为空：{path}");

    public static void Write<T>(string path, T value)
    {
        var temporary = path + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Options);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
}
