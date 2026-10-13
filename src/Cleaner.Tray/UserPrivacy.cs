using Cleaner.Core;
using Microsoft.Win32;

namespace Cleaner.Tray;

internal static class UserPrivacy
{
    private static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsCleaner");
    private static RegistryJournal Journal => new(Registry.CurrentUser, Path.Combine(DirectoryPath, "privacy-rollback.json"));
    public static void Apply()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (SafeCleanup.HasLinkedAncestor(DirectoryPath)) throw new IOException("User settings directory must not be linked.");
        Journal.Apply([
            (@"Software\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1),
            (@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0)
        ]);
    }
    public static void Restore()
    {
        if (!Directory.Exists(DirectoryPath)) return;
        if (SafeCleanup.HasLinkedAncestor(DirectoryPath)) throw new IOException("User settings directory must not be linked.");
        Journal.Restore();
    }
}
