using System;
using System.Diagnostics;
using System.IO;

internal static class Launcher
{
    [STAThread]
    private static int Main(string[] args)
    {
        System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        try
        {
            var root = AppDomain.CurrentDomain.BaseDirectory;
            var argument = args.Length == 1 && (args[0] == "--quit" || args[0] == "--restore-user" || args[0] == "--preview") ? " " + args[0] : "";
            var start = new ProcessStartInfo(Path.Combine(root, "Runtime", "dotnet.exe"),
                "\"" + Path.Combine(root, "Tray", "Cleaner.Tray.dll") + "\"" + argument);
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WorkingDirectory = root;
            using (var process = Process.Start(start))
            {
                if (argument.Length > 0 && args[0] != "--preview") { process.WaitForExit(20000); return process.HasExited ? process.ExitCode : 1; }
            }
            return 0;
        }
        catch (Exception ex)
        {
            System.Windows.Forms.MessageBox.Show(ex.Message, "Windows Cleaner", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
            return 1;
        }
    }
}
