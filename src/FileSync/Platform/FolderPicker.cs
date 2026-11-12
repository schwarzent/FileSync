using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FileSync.Platform;

public interface IFolderPicker
{
    Task<string?> Pick(string? initialPath);
}

public sealed class FolderPicker : IFolderPicker
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<string?> Pick(string? initialPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("文件夹选择框需要 Windows。");
        if (!await gate.WaitAsync(0)) throw new Core.UserError("文件夹选择框已打开，请先完成选择。", 409);
        try { return await PickOnThread(initialPath); }
        finally { gate.Release(); }
    }

    [SupportedOSPlatform("windows")]
    private static Task<string?> PickOnThread(string? initialPath)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = NativeFolderDialog.GetForegroundWindow();
        var thread = new Thread(() =>
        {
            try { completion.SetResult(NativeFolderDialog.Pick(initialPath, owner)); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true, Name = "FileSync folder picker" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}

[SupportedOSPlatform("windows")]
internal static class NativeFolderDialog
{
    private const uint PickFolders = 0x20;
    private const uint ForceFileSystem = 0x40;
    private const uint PathMustExist = 0x800;
    private const uint FileSystemPath = 0x80058000;
    private const int Cancelled = unchecked((int)0x800704C7);
    private const uint SingleThreadedApartment = 2;
    private static readonly Guid DialogClass = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid ShellItemInterface = typeof(IShellItem).GUID;

    public static string? Pick(string? initialPath, IntPtr owner)
    {
        Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, SingleThreadedApartment));
        try { return Show(initialPath, owner); }
        finally { CoUninitialize(); }
    }

    private static string? Show(string? initialPath, IntPtr owner)
    {
        var type = Type.GetTypeFromCLSID(DialogClass, throwOnError: true)!;
        var dialog = (IFileDialog)Activator.CreateInstance(type)!;
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | PickFolders | ForceFileSystem | PathMustExist);
            dialog.SetTitle("选择 FileSync 文件夹（可在地址栏粘贴路径）");
            if (!string.IsNullOrWhiteSpace(initialPath)) SetInitialFolder(dialog, initialPath);
            var result = dialog.Show(owner);
            if (result == Cancelled) return null;
            Marshal.ThrowExceptionForHR(result);
            dialog.GetResult(out var item);
            try { item.GetDisplayName(FileSystemPath, out var path); return path; }
            finally { Marshal.ReleaseComObject(item); }
        }
        finally { Marshal.ReleaseComObject(dialog); }
    }

    private static void SetInitialFolder(IFileDialog dialog, string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new Core.UserError("填写的文件夹不存在，请修改路径或清空后浏览。");
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ShellItemInterface, out var item));
        try { dialog.SetFolder(item); }
        finally { Marshal.ReleaseComObject(item); }
    }

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context,
        [In] in Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    // 未使用的方法仍保留原生 COM 的槽位顺序。
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes();
        void SetFileTypeIndex();
        void GetFileTypeIndex();
        void Advise();
        void Unadvise();
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder();
        void GetCurrentSelection();
        void SetFileName();
        void GetFileName();
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel();
        void SetFileNameLabel();
        void GetResult(out IShellItem item);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler();
        void GetParent();
        void GetDisplayName(uint format, [MarshalAs(UnmanagedType.LPWStr)] out string name);
    }
}
