using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Cleaner.Service;

internal static class NativeService
{
    private delegate void MainCallback(int argc, IntPtr argv);
    private delegate uint ControlCallback(uint control, uint eventType, IntPtr eventData, IntPtr context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TableEntry { [MarshalAs(UnmanagedType.LPWStr)] public string? Name; public MainCallback? Main; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    { public uint Type, State, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint; }
    private static readonly CancellationTokenSource cancellation = new();
    private static IntPtr handle;
    private static readonly MainCallback main = ServiceMain;
    private static readonly ControlCallback control = Control;
    private static Func<CancellationToken, Task>? run;
    private static readonly object statusLock = new();

    public static int Run(Func<CancellationToken, Task> work)
    {
        run = work;
        var table = new[] { new TableEntry { Name = Cleaner.Core.Product.ServiceName, Main = main }, new TableEntry() };
        if (!StartServiceCtrlDispatcher(table)) return Marshal.GetLastWin32Error();
        return 0;
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        handle = RegisterServiceCtrlHandlerEx(Cleaner.Core.Product.ServiceName, control, IntPtr.Zero);
        if (handle == IntPtr.Zero) return;
        Report(2, 0, 1, 30000);
        uint exit = 0;
        try
        {
            Report(4, 0);
            run!(cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch { exit = 1064; }
        finally { Report(1, exit); }
    }

    private static uint Control(uint command, uint eventType, IntPtr eventData, IntPtr context)
    {
        if (command is 1 or 5)
        {
            Report(3, 0, 1, 30000);
            cancellation.Cancel();
        }
        else if (command == 4) Report(cancellation.IsCancellationRequested ? 3u : 4u, 0);
        return 0;
    }

    private static void Report(uint state, uint exit, uint checkpoint = 0, uint waitHint = 0)
    {
        lock (statusLock)
        {
            var status = new ServiceStatus { Type = 0x10, State = state, Controls = state == 4 ? 5u : 0,
                Win32Exit = exit, Checkpoint = checkpoint, WaitHint = waitHint };
            if (!SetServiceStatus(handle, ref status)) throw new Win32Exception();
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher([In] TableEntry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string name, ControlCallback callback, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
}
