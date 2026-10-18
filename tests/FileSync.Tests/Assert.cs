namespace FileSync.Tests;

public static class Check
{
    private static int count;
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("验证失败：" + message);
        Console.WriteLine($"PASS {++count:00} {message}");
    }

    public static async Task Reject(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception error) when (error is not InvalidOperationException)
        { True(true, message); return; }
        throw new InvalidOperationException("未拒绝：" + message);
    }

    public static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is not InvalidOperationException)
        { True(true, message); return; }
        throw new InvalidOperationException("未拒绝：" + message);
    }
}
