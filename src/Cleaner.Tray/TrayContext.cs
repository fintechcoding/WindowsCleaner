using Cleaner.Core;
using System.ComponentModel;
using System.Diagnostics;

namespace Cleaner.Tray;

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon icon;
    private readonly ToolStripMenuItem start = new("Start");
    private readonly ToolStripMenuItem stop = new("Stop");
    private readonly ToolStripMenuItem clean = new("Clean now");
    private readonly ToolStripMenuItem telemetry = new("Enable telemetry blocking...");
    private readonly System.Windows.Forms.Timer pollTimer = new() { Interval = 2000 };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? userCancellation;
    private Task userCleanup = Task.CompletedTask;
    private Status? status;
    private CleanupResult? userResult;
    private string? userError;
    private string? connectionError;
    private string? lastInstance;
    private long lastCycle;
    private bool polling;
    private bool closing;
    private bool disposed;
    private bool? privacyApplied;
    private Form? statusWindow;
    private TextBox? statusText;
    private readonly Control dispatcher = new();
    private readonly bool preview;

    public TrayContext(bool preview = false)
    {
        this.preview = preview;
        _ = dispatcher.Handle;
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([start, stop, clean, new ToolStripSeparator(), new ToolStripMenuItem("Status", null, (_, _) => ShowStatus()),
            telemetry, new ToolStripSeparator(), new ToolStripMenuItem("Exit tray", null, async (_, _) => await Quit())]);
        icon = new NotifyIcon { Icon = SystemIcons.Shield, Text = "Windows Cleaner: connecting", ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => ShowStatus();
        start.Click += async (_, _) => await Command("start");
        stop.Click += async (_, _) => { userCancellation?.Cancel(); await Command("stop"); };
        clean.Click += async (_, _) => await Command("clean");
        telemetry.Click += async (_, _) => await ToggleTelemetry();
        pollTimer.Tick += async (_, _) => await Poll();
        if (preview)
        {
            status = new Status(false, false, false, "preview", 0, null, null, null);
            Refresh();
            ShowStatus();
        }
        else { pollTimer.Start(); _ = Poll(); }
        _ = ListenForShutdown();
    }

    private async Task ListenForShutdown()
    {
        try
        {
            using var pipe = new System.IO.Pipes.NamedPipeServerStream("WindowsCleaner.Tray." + Process.GetCurrentProcess().SessionId,
                System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte,
                System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly | System.IO.Pipes.PipeOptions.FirstPipeInstance);
            while (!lifetime.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(lifetime.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var reader = new StreamReader(pipe, leaveOpen: true);
                var command = await PipeProtocol.ReadLineAsync(reader, timeout.Token);
                if (command == "quit")
                {
                    dispatcher.BeginInvoke(new Action(async () => await Quit(() =>
                    {
                        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                        writer.WriteLine("ok");
                    })));
                    await Task.Delay(Timeout.Infinite, lifetime.Token);
                }
                if (pipe.IsConnected) pipe.Disconnect();
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        { if (!closing) userError = "Tray shutdown channel unavailable: " + ex.Message; }
    }

    private async Task Poll()
    {
        if (polling || closing) return;
        polling = true;
        try
        {
            var response = await PipeProtocol.SendAsync("status", lifetime.Token);
            if (closing) return;
            connectionError = null;
            Accept(response);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            if (closing) return;
            connectionError = "Service unavailable. " + ex.Message;
            userCancellation?.Cancel();
            try { UserPrivacy.Restore(); privacyApplied = false; }
            catch (Exception restoreError) { userError = restoreError.Message; }
            // If installation files were removed, repeat the best-effort current-user restoration.
            var serviceExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Service", "Cleaner.Service.dll"));
            if (!File.Exists(serviceExe))
            {
                try { UserPrivacy.Restore(); } catch (Exception restoreError) { userError = restoreError.Message; }
            }
        }
        finally { polling = false; if (!closing) Refresh(); }
    }

    private void Accept(Status response)
    {
        if (closing) return;
        status = response;
        if (!response.Running) userCancellation?.Cancel();
        if (privacyApplied != response.TelemetryBlocked)
        {
            try
            {
                if (response.TelemetryBlocked) UserPrivacy.Apply(); else UserPrivacy.Restore();
                privacyApplied = response.TelemetryBlocked;
            }
            catch (Exception ex) { userError = "User privacy settings failed: " + ex.Message; }
        }
        if (response.Running && response.Cycle > 0 && userCleanup.IsCompleted &&
            (response.InstanceId != lastInstance || response.Cycle != lastCycle))
        {
            lastInstance = response.InstanceId;
            lastCycle = response.Cycle;
            userCancellation?.Dispose();
            userCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            userCleanup = CleanUser(userCancellation.Token);
        }
    }

    private async Task CleanUser(CancellationToken cancellation)
    {
        try { userResult = await Task.Run(() => SafeCleanup.User(cancellation), cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { userError = "User cleanup failed: " + ex.Message; }
        if (!closing) Refresh();
    }

    private async Task Command(string command)
    {
        if (closing || preview) return;
        try
        {
            var response = await PipeProtocol.SendAsync(command, lifetime.Token);
            Accept(response);
            if (response.Error != null) MessageBox.Show(response.Error, "Windows Cleaner", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Windows Cleaner", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        if (!closing) Refresh();
    }

    private async Task ToggleTelemetry()
    {
        if (status == null) return;
        try
        {
            telemetry.Enabled = false;
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!, Arguments =
                    (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)
                        ? "\"" + Path.Combine(AppContext.BaseDirectory, "Cleaner.Tray.dll") + "\" " : "") +
                    "--telemetry " + (status.TelemetryBlocked ? "off" : "on"),
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory
            });
            if (process != null) await process.WaitForExitAsync(lifetime.Token);
            await Poll();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Windows Cleaner", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { if (!closing) Refresh(); }
    }

    private void Refresh()
    {
        var connected = !preview && connectionError == null && status != null;
        start.Enabled = connected && !status!.Running;
        stop.Enabled = connected && status!.Running;
        clean.Enabled = connected && status!.Running && !status.Busy && userCleanup.IsCompleted;
        telemetry.Enabled = connected && !status!.Busy;
        telemetry.Text = status?.TelemetryBlocked == true ? "Disable telemetry blocking..." : "Enable telemetry blocking...";
        icon.Text = preview ? "Windows Cleaner: preview" : !connected ? "Windows Cleaner: service unavailable" : status!.Busy || !userCleanup.IsCompleted
            ? "Windows Cleaner: cleaning" : status.Running ? "Windows Cleaner: running" : "Windows Cleaner: stopped";
        if (statusText is { IsDisposed: false }) statusText.Text = Describe();
    }

    private string Describe()
    {
        if (connectionError != null) return connectionError;
        if (status == null) return "Connecting to the Windows Cleaner service...";
        static string Time(DateTimeOffset? value) => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "None";
        static string Result(string name, CleanupResult? result) => result == null ? $"{name}: No completed cleanup." :
            $"{name}: {result.Deleted} items cleared; {result.Skipped} skipped.\r\n" + string.Join("\r\n", result.Errors ?? []);
        return (preview ? "Preview mode: no system changes.\r\n\r\n" : "") + $"Cleanup: {(status.Running ? "Running" : "Stopped")}\r\n" +
            $"Activity: {(status.Busy || !userCleanup.IsCompleted ? "Working" : "Idle")}\r\nInterval: 5 minutes\r\n" +
            $"Telemetry blocking: {(status.TelemetryBlocked ? "Enabled" : "Disabled")}\r\n" +
            $"Last system cleanup: {Time(status.LastRun)}\r\nNext system cleanup: {Time(status.NextRun)}\r\n\r\n" +
            Result("System", status.Result) + "\r\n\r\n" + Result("Current user", userResult) +
            $"\r\n\r\n{status.Error}\r\n{userError}\r\n\r\n" +
            "Locked files and inaccessible event channels are skipped. Windows can create new events after cleanup. " +
            "Application diagnostics are kept in memory only. Active TCP connections and saved RDP passwords are preserved.";
    }

    private void ShowStatus()
    {
        if (statusWindow is { IsDisposed: false }) { statusWindow.Show(); statusWindow.Activate(); return; }
        statusText = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical,
            BackColor = SystemColors.Window, Text = Describe(), BorderStyle = BorderStyle.None };
        statusWindow = new Form { Text = "Windows Cleaner - Status", Width = 650, Height = 480,
            StartPosition = FormStartPosition.CenterScreen, Icon = SystemIcons.Shield, Padding = new Padding(16) };
        statusWindow.Controls.Add(statusText);
        statusWindow.Show();
    }

    private async Task Quit(Action? acknowledge = null)
    {
        if (closing) return;
        closing = true;
        pollTimer.Stop();
        userCancellation?.Cancel();
        await userCleanup;
        if (!preview) { try { UserPrivacy.Restore(); } catch { } }
        try { acknowledge?.Invoke(); } catch (IOException) { }
        lifetime.Cancel();
        icon.Visible = false;
        statusWindow?.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            closing = true;
            lifetime.Cancel(); pollTimer.Dispose(); icon.Dispose();
            dispatcher.Dispose();
            userCancellation?.Dispose(); lifetime.Dispose(); statusWindow?.Dispose();
        }
        base.Dispose(disposing);
    }
}
