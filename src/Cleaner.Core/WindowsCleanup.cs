using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Cleaner.Core;

public static class WindowsCleanup
{
    public static CleanupResult System(CancellationToken cancellation)
    {
        var result = new ResultBuilder();
        result.Add(SafeCleanup.DirectoryContents(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), cancellation));
        cancellation.ThrowIfCancellationRequested();
        if (!DnsFlushResolverCache()) result.Skip("DNS cache could not be flushed.");
        try { EnableSecurityPrivilege(); }
        catch (Win32Exception ex) { result.Skip($"Security log privilege could not be enabled (Windows error {ex.NativeErrorCode})."); }
        var enumeration = EvtOpenChannelEnum(IntPtr.Zero, 0);
        if (enumeration == IntPtr.Zero) { result.Skip("Event channels could not be enumerated."); return result.Build(); }
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var channel = new StringBuilder(1024);
                if (!EvtNextChannelPath(enumeration, channel.Capacity, channel, out var needed))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    if (error == 122)
                    {
                        channel = new StringBuilder(needed);
                        if (!EvtNextChannelPath(enumeration, channel.Capacity, channel, out _))
                        { result.Skip("Event channel enumeration failed."); break; }
                    }
                    else { result.Skip($"Event channel enumeration failed (Windows error {error})."); break; }
                }
                if (EvtClearLog(IntPtr.Zero, channel.ToString(), null, 0)) result.Deleted++;
                else result.Skip($"Event channel skipped: {channel} ({Marshal.GetLastWin32Error()}).");
            }
        }
        finally { EvtClose(enumeration); }
        return result.Build();
    }

    private static void EnableSecurityPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x8, out var token)) throw new Win32Exception();
        try
        {
            if (!LookupPrivilegeValue(null, "SeSecurityPrivilege", out var luid)) throw new Win32Exception();
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            var error = Marshal.GetLastWin32Error();
            if (error != 0) throw new Win32Exception(error);
        }
        finally { CloseHandle(token); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("dnsapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DnsFlushResolverCache();
    [DllImport("wevtapi.dll", SetLastError = true)] private static extern IntPtr EvtOpenChannelEnum(IntPtr session, int flags);
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtNextChannelPath(IntPtr enumeration, int size, StringBuilder path, out int used);
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtClearLog(IntPtr session, string channel, string? target, int flags);
    [DllImport("wevtapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EvtClose(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disable, ref TokenPrivileges privileges, int length, IntPtr previous, IntPtr returned);
}
