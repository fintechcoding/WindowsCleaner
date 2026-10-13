using Cleaner.Core;
using System.IO.Pipes;

namespace Cleaner.Tray;

internal static class EntryPoint
{
    [STAThread]
    private static int Main(string[] args)
    {
        English.Initialize();
        ApplicationConfiguration.Initialize();
        if (args is ["--quit"])
        {
            QuitExisting().GetAwaiter().GetResult();
            return 0;
        }
        if (args is ["--telemetry", "on"] or ["--telemetry", "off"])
        {
            try
            {
                var response = PipeProtocol.SendAsync(args[1] == "on" ? "telemetry-on" : "telemetry-off").GetAwaiter().GetResult();
                if (response.Error != null) throw new IOException(response.Error);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Windows Cleaner", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
        if (args is ["--restore-user"])
        {
            try { UserPrivacy.Restore(); return 0; }
            catch { return 1; }
        }
        var preview = args is ["--preview"];
        if (args.Length != 0 && !preview) return 87;
        using var singleInstance = new Mutex(true, @"Local\WindowsCleaner.Tray." + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, out var created);
        if (!created) return 0;
        using var tray = new TrayContext(preview);
        Application.Run(tray);
        return 0;
    }

    private static async Task QuitExisting()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", "WindowsCleaner.Tray." + System.Diagnostics.Process.GetCurrentProcess().SessionId,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await pipe.ConnectAsync(1000, cancellation.Token);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await writer.WriteLineAsync("quit");
            await reader.ReadLineAsync(cancellation.Token);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { }
    }
}
