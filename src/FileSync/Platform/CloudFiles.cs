using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileSync.Platform;

public static class CloudFiles
{
    private const uint CloudTag = 0x9000001A;
    private const uint CloudTagMask = 0xFFFF0FFF;
    private const uint ReadAttributes = 0x80;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const int AttributeTagInformation = 9;

    public static bool IsPlaceholder(string path)
    {
        using var handle = CreateFile(path, ReadAttributes, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandleEx(handle, AttributeTagInformation, out var data,
                (uint)Marshal.SizeOf<AttributeTagInfo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return IsCloudTag(data.ReparseTag);
    }

    public static bool IsCloudTag(uint tag) => (tag & CloudTagMask) == CloudTag;

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo
    {
        public uint Attributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out AttributeTagInfo information, uint size);
}
