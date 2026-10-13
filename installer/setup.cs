using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Setup
{
    private const string ServiceName = "WindowsCleaner";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WindowsCleaner";
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsCleaner");
    private static readonly string Runtime = Path.Combine(Root, "Runtime", "dotnet.exe");
    private static readonly string ServiceDll = Path.Combine(Root, "Service", "Cleaner.Service.dll");

    [STAThread]
    private static int Main(string[] args)
    {
        Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        if (args.Length == 1 && args[0] == "--verify-payload")
        {
            try
            {
                using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("WindowsCleaner.Payload.zip"))
                using (var archive = new ZipArchive(resource, ZipArchiveMode.Read))
                {
                    foreach (var required in new[] { "Runtime/dotnet.exe", "Tray/Cleaner.Tray.dll", "Service/Cleaner.Service.dll", "WindowsCleaner.exe", "Uninstall.exe" })
                        if (!archive.Entries.Any(e => e.FullName.Replace('\\', '/') == required)) return 2;
                    foreach (var entry in archive.Entries)
                        if (entry.FullName.Contains("..") || Path.IsPathRooted(entry.FullName)) return 3;
                }
                return 0;
            }
            catch { return 1; }
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool uninstall = args.Length == 1 && args[0] == "--uninstall";
        if (args.Length != 0 && !uninstall) return 87;
        bool created;
        using (var mutex = new Mutex(true, @"Global\WindowsCleaner.Setup", out created))
        {
            if (!created) { MessageBox.Show("Another setup is already running.", "Windows Cleaner"); return 1; }
            using (var form = new Form())
            {
                form.Text = uninstall ? "Uninstall Windows Cleaner" : "Windows Cleaner Setup";
                form.ClientSize = new System.Drawing.Size(540, 285);
                form.StartPosition = FormStartPosition.CenterScreen;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                var title = new Label { Text = uninstall ? "Remove Windows Cleaner" : "Install Windows Cleaner", Left = 24, Top = 20, Width = 490, Height = 30,
                    Font = new System.Drawing.Font("Segoe UI", 16) };
                var description = new Label { Left = 24, Top = 65, Width = 490, Height = 110, Text = uninstall
                    ? "Setup will stop cleanup, restore managed telemetry settings, and remove the application. Already deleted data cannot be restored."
                    : "Runs in the system tray with Start and Stop controls. Cleans RDP history, bitmap cache, temp files, DNS cache, and accessible Windows event logs every 5 minutes. Applies reversible telemetry blocking. Cleanup starts immediately after installation." };
                var location = new Label { Left = 24, Top = 175, Width = 490, Height = 25, Text = "Location: " + Root };
                var progress = new Label { Left = 24, Top = 205, Width = 300, Height = 45, Text = "Ready." };
                var action = new Button { Left = 340, Top = 230, Width = 85, Height = 30, Text = uninstall ? "Uninstall" : "Install" };
                var cancel = new Button { Left = 435, Top = 230, Width = 85, Height = 30, Text = "Cancel" };
                var busy = false;
                cancel.Click += delegate { form.Close(); };
                form.FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
                action.Click += async delegate
                {
                    busy = true; action.Enabled = false; cancel.Enabled = false;
                    progress.Text = uninstall ? "Removing..." : "Installing...";
                    try
                    {
                        await Task.Run(() => { if (uninstall) Uninstall(); else Install(); });
                        progress.Text = uninstall ? "Removed successfully." : "Installed successfully.";
                        MessageBox.Show(uninstall ? "Windows Cleaner has been removed."
                            : "Windows Cleaner is installed. Open Windows Cleaner from the Start menu if its tray icon is not visible.",
                            "Windows Cleaner", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        busy = false; form.Close();
                    }
                    catch (Exception ex)
                    {
                        progress.Text = "Setup failed.";
                        MessageBox.Show(ex.Message + "\r\n\r\nNo setup log was written. You can retry setup.", "Windows Cleaner", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        busy = false; action.Enabled = true; cancel.Enabled = true;
                    }
                };
                form.Controls.AddRange(new Control[] { title, description, location, progress, action, cancel });
                Application.Run(form);
            }
        }
        return 0;
    }

    private static void Install()
    {
        if (!Environment.Is64BitOperatingSystem || Environment.OSVersion.Version.Build < 19045)
            throw new NotSupportedException("Windows 10 22H2 or Windows 11 x64 is required.");
        CheckPath(Root);
        StopExisting();
        var temporary = Path.Combine(Path.GetTempPath(), "WindowsCleaner-Setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("WindowsCleaner.Payload.zip"))
            using (var archive = new ZipArchive(resource, ZipArchiveMode.Read)) archive.ExtractToDirectory(temporary);
            Directory.CreateDirectory(Root);
            SecureDirectory(Root);
            foreach (var source in Directory.GetFiles(temporary, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(Root, source.Substring(temporary.Length + 1));
                CheckPath(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(source, destination, true);
            }
            Run(Runtime, ServiceDll, "--initialize");
            var command = Quote(Runtime) + " " + Quote(ServiceDll);
            if (ServiceExists()) Run(Sc, "config", ServiceName, "binPath=", command, "start=", "auto");
            else Run(Sc, "create", ServiceName, "binPath=", command, "start=", "auto", "DisplayName=", "Windows Cleaner");
            Run(Sc, "description", ServiceName, "Scheduled privacy and temporary-file maintenance with system tray controls.");
            Run(Sc, "failure", ServiceName, "reset=", "86400", "actions=", "restart/30000/restart/60000/restart/120000");
            using (var run = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"))
                run.SetValue("WindowsCleaner", Quote(Path.Combine(Root, "WindowsCleaner.exe")));
            using (var key = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "Windows Cleaner"); key.SetValue("DisplayVersion", "1.0.0");
                key.SetValue("Publisher", "Windows Cleaner"); key.SetValue("InstallLocation", Root);
                key.SetValue("UninstallString", Quote(Path.Combine(Root, "Uninstall.exe")) + " --uninstall");
                key.SetValue("DisplayIcon", Path.Combine(Root, "WindowsCleaner.exe"));
                key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1);
            }
            CreateShortcut();
            Run(Sc, "start", ServiceName);
            try { LaunchUnelevated(); } catch { /* The Start menu remains available. */ }
        }
        finally { try { CheckPath(temporary); Directory.Delete(temporary, true); } catch { } }
    }

    private static void Uninstall()
    {
        CheckPath(Root);
        StopExisting();
        if (File.Exists(Runtime) && File.Exists(ServiceDll)) Run(Runtime, ServiceDll, "--restore");
        if (ServiceExists()) Run(Sc, "delete", ServiceName);
        using (var run = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            if (run != null) run.DeleteValue("WindowsCleaner", false);
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Windows Cleaner.lnk");
        if (File.Exists(shortcut)) File.Delete(shortcut);
        if (Directory.Exists(Root))
        {
            foreach (var entry in Directory.GetFileSystemEntries(Root))
            {
                CheckPath(entry);
                if (string.Equals(entry, Assembly.GetExecutingAssembly().Location, StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(entry)) { CheckTree(entry); Directory.Delete(entry, true); }
                else File.Delete(entry);
            }
        }
        // The running uninstaller and root are removed at the next reboot using the supported OS API.
        MoveFileEx(Path.Combine(Root, "Uninstall.exe"), null, 4);
        MoveFileEx(Root, null, 4);
    }

    private static string Sc { get { return Path.Combine(Environment.SystemDirectory, "sc.exe"); } }
    private static bool ServiceExists() { return ServiceController.GetServices().Any(s => { using (s) return s.ServiceName == ServiceName; }); }
    private static void StopExisting()
    {
        if (!ServiceExists()) return;
        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName))
        {
            var command = key == null ? "" : key.GetValue("ImagePath", "").ToString();
            if (!command.StartsWith(Quote(Runtime) + " " + Quote(ServiceDll), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The WindowsCleaner service name is already used by another installation.");
        }
        var launcher = Path.Combine(Root, "WindowsCleaner.exe");
        if (File.Exists(launcher)) Run(launcher, "--quit");
        using (var service = new ServiceController(ServiceName))
        {
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
            }
        }
    }

    private static void Run(string filename, params string[] args)
    {
        var start = new ProcessStartInfo(filename, string.Join(" ", args.Select(Quote)))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var process = Process.Start(start))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000)) { process.Kill(); throw new System.TimeoutException("Setup command timed out."); }
            if (process.ExitCode != 0) throw new InvalidOperationException("Setup command failed: " + Path.GetFileName(filename) + " (exit code " + process.ExitCode + ").");
        }
    }

    private static string Quote(string value)
    {
        var text = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { text.Append('\\', slashes * 2 + 1); text.Append(c); }
            else { text.Append('\\', slashes); text.Append(c); }
            slashes = 0;
        }
        text.Append('\\', slashes * 2); text.Append('"'); return text.ToString();
    }
    private static void CheckPath(string path)
    {
        string current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Setup cannot use linked files or folders: " + current);
            current = Path.GetDirectoryName(current);
        }
    }
    private static void CheckTree(string directory)
    {
        CheckPath(directory);
        foreach (var entry in Directory.GetFileSystemEntries(directory))
        { CheckPath(entry); if (Directory.Exists(entry)) CheckTree(entry); }
    }
    private static void SecureDirectory(string directory)
    {
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        Directory.SetAccessControl(directory, acl);
    }
    private static void CreateShortcut()
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic shortcut = shell.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Windows Cleaner.lnk"));
        shortcut.TargetPath = Path.Combine(Root, "WindowsCleaner.exe"); shortcut.WorkingDirectory = Root; shortcut.Save();
    }
    private static void LaunchUnelevated()
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); scheduler.Connect();
        dynamic folder = scheduler.GetFolder("\\"); dynamic task = scheduler.NewTask(0);
        task.Principal.UserId = WindowsIdentity.GetCurrent().Name; task.Principal.LogonType = 3; task.Principal.RunLevel = 0;
        task.Settings.ExecutionTimeLimit = "PT0S";
        dynamic action = task.Actions.Create(0); action.Path = Path.Combine(Root, "WindowsCleaner.exe");
        string name = "WindowsCleaner.Launch." + Guid.NewGuid().ToString("N");
        dynamic registered = folder.RegisterTaskDefinition(name, task, 6, WindowsIdentity.GetCurrent().Name, null, 3);
        registered.Run(null); Thread.Sleep(500); folder.DeleteTask(name, 0);
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string source, string destination, int flags);
}
