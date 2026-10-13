using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cleaner.Core;

public static class PipeProtocol
{
    public static async Task<Status> SendAsync(string command, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeClientStream(".", Product.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await pipe.ConnectAsync(timeout.Token);
        ValidateServer(pipe.SafePipeHandle);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await pipe.WriteAsync(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Request(command)) + "\n"), timeout.Token);
        var line = await ReadLineAsync(reader, timeout.Token);
        await pipe.WriteAsync("ack\n"u8.ToArray(), timeout.Token);
        return JsonSerializer.Deserialize<Status>(line) ?? throw new IOException("Invalid service response.");
    }

    private static void ValidateServer(SafePipeHandle pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe, out var serverPid)) throw new IOException("Could not authenticate the service connection.");
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new IOException("Could not query the registered service.");
        try
        {
            var service = OpenService(manager, Product.ServiceName, 4);
            if (service == IntPtr.Zero) throw new IOException("Windows Cleaner is not installed as a service.");
            try
            {
                if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceProcessStatus>(), out _) ||
                    status.ProcessId == 0 || status.ProcessId != serverPid)
                    throw new IOException("The pipe server is not the registered Windows Cleaner service.");
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceProcessStatus
    { public uint Type, State, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint, ProcessId, Flags; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceProcessStatus status, int size, out int needed);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    public static async Task<string> ReadLineAsync(StreamReader reader, CancellationToken cancellation)
    {
        var buffer = new char[1];
        var text = new System.Text.StringBuilder();
        while (await reader.ReadAsync(buffer.AsMemory(), cancellation) != 0)
        {
            if (buffer[0] == '\n') return text.ToString();
            if (buffer[0] != '\r') text.Append(buffer[0]);
            if (text.Length > 32768) throw new IOException("Pipe message is too large.");
        }
        throw new IOException("The service connection closed unexpectedly.");
    }
}
