namespace FileSync.Core;

public sealed class InstanceLock : IDisposable
{
    private readonly FileStream stream;

    public InstanceLock(StateStore store)
    {
        try
        {
            stream = new(Path.Combine(store.Root, "instance.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        { throw new UserError($"此数据目录已被另一个实例使用：{error.Message}", 409); }
    }

    public void Dispose() => stream.Dispose();
}
