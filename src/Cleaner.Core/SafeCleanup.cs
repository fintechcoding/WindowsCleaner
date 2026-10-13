using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Cleaner.Core;

public static class SafeCleanup
{
    public static bool HasLinkedAncestor(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current != null; current = current.Parent)
        {
            if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }

    public static CleanupResult DirectoryContents(string root, CancellationToken cancellation = default)
    {
        var result = new ResultBuilder();
        if (!Directory.Exists(root)) return result.Build();
        try
        {
            root = Path.GetFullPath(root);
            if (Path.GetPathRoot(root) == root || HasLinkedAncestor(root))
                throw new IOException("Unsafe cleanup root; linked directories and volume roots are excluded.");
            Visit(root, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Skip(ex.Message); }
        return result.Build();

        void Visit(string directory, int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            if (depth > 64) { result.Skip("Directory nesting limit reached."); return; }
            try
            {
                // Excluding FILE_SHARE_DELETE pins each directory while walking it. A junction
                // replacement cannot redirect an elevated cleanup between checking and deleting.
                using var pinned = OpenEntry(directory, 0x80);
                var directoryAttributes = Attributes(pinned);
                if ((directoryAttributes & FileAttributes.ReparsePoint) != 0 || (directoryAttributes & FileAttributes.Directory) == 0)
                { result.Skip("Linked or replaced directory skipped."); return; }
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { result.Skip("Linked entry skipped."); continue; }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            Visit(entry, depth + 1);
                            Directory.Delete(entry, false);
                        }
                        else
                        {
                            using var file = OpenEntry(entry, 0x10000 | 0x80);
                            if ((Attributes(file) & FileAttributes.ReparsePoint) != 0) { result.Skip("Linked file skipped."); continue; }
                            var disposition = new FileDisposition { Delete = true };
                            if (!SetFileInformationByHandle(file, 4, ref disposition, Marshal.SizeOf<FileDisposition>()))
                                throw new IOException($"File deletion failed (Windows error {Marshal.GetLastWin32Error()}).");
                            result.Deleted++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Skip(ex.Message); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Skip(ex.Message); }
        }
    }

    private static SafeFileHandle OpenEntry(string path, uint access)
    {
        var handle = CreateFile(path, access, 1 | 2, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new IOException($"File access failed (Windows error {error}).");
        }
        return handle;
    }
    private static FileAttributes Attributes(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out var info, Marshal.SizeOf<AttributeTag>()))
            throw new IOException($"File attribute query failed (Windows error {Marshal.GetLastWin32Error()}).");
        return info.Attributes;
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public FileAttributes Attributes; public uint Tag; }
    [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { [MarshalAs(UnmanagedType.Bool)] public bool Delete; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out AttributeTag info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref FileDisposition disposition, int size);

    public static CleanupResult User(CancellationToken cancellation = default)
    {
        var result = new ResultBuilder();
        result.Add(DirectoryContents(Path.GetTempPath(), cancellation));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        result.Add(DirectoryContents(Path.Combine(local, "Microsoft", "Terminal Server Client", "Cache"), cancellation));
        cancellation.ThrowIfCancellationRequested();
        try
        {
            using var client = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Terminal Server Client", true);
            if (client != null)
            {
                using var defaults = client.OpenSubKey("Default", true);
                foreach (var name in defaults?.GetValueNames() ?? [])
                    if (name.StartsWith("MRU", StringComparison.OrdinalIgnoreCase)) { defaults!.DeleteValue(name); result.Deleted++; }
                client.DeleteSubKeyTree("Servers", false);
            }
            using var recent = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs", true);
            recent?.DeleteSubKeyTree(".rdp", false);
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            DeleteFile(Path.Combine(documents, "Default.rdp"));
            var recentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (Directory.Exists(recentDirectory) && !HasLinkedAncestor(recentDirectory))
                foreach (var file in Directory.EnumerateFiles(recentDirectory, "*.rdp.lnk")) DeleteFile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { result.Skip(ex.Message); }
        return result.Build();

        void DeleteFile(string path)
        {
            if (!File.Exists(path)) return;
            if (HasLinkedAncestor(Path.GetDirectoryName(path)!) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            { result.Skip("Linked RDP history file skipped."); return; }
            File.Delete(path); result.Deleted++;
        }
    }
}
