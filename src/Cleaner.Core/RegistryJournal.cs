using Microsoft.Win32;
using System.Text.Json;

namespace Cleaner.Core;

public sealed record RegistryChange(string Path, string Name, int Applied, bool Existed, RegistryValueKind Kind, JsonElement Original);

public sealed record RegistrySnapshot(bool Existed, RegistryValueKind Kind, JsonElement Value);
public interface IRegistryStore
{
    RegistrySnapshot Read(string path, string name);
    void Write(string path, string name, object value, RegistryValueKind kind);
    void Delete(string path, string name);
}
public sealed class WindowsRegistryStore(RegistryKey root) : IRegistryStore
{
    public RegistrySnapshot Read(string path, string name)
    {
        using var key = root.OpenSubKey(path);
        var exists = key?.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) == true;
        return new(exists, exists ? key!.GetValueKind(name) : RegistryValueKind.DWord,
            JsonSerializer.SerializeToElement(key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)));
    }
    public void Write(string path, string name, object value, RegistryValueKind kind)
    { using var key = root.CreateSubKey(path, true); key.SetValue(name, value, kind); }
    public void Delete(string path, string name)
    { using var key = root.OpenSubKey(path, true); key?.DeleteValue(name, false); }
}

public sealed class RegistryJournal(IRegistryStore store, string journalPath)
{
    public RegistryJournal(RegistryKey root, string journalPath) : this(new WindowsRegistryStore(root), journalPath) { }
    public void Apply(IEnumerable<(string Path, string Name, int Value)> changes)
    {
        var journal = JsonFiles.Read<List<RegistryChange>>(journalPath, () => []);
        foreach (var (path, name, value) in changes)
        {
            var entry = journal.Find(x => x.Path == path && x.Name == name);
            if (entry == null)
            {
                var snapshot = store.Read(path, name);
                entry = new RegistryChange(path, name, value, snapshot.Existed, snapshot.Kind, snapshot.Value);
                journal.Add(entry);
                JsonFiles.Write(journalPath, journal); // Write-ahead: survive interruption between capture and mutation.
            }
            store.Write(path, name, entry.Applied, RegistryValueKind.DWord);
        }
    }

    public string[] Restore()
    {
        var journal = JsonFiles.Read<List<RegistryChange>>(journalPath, () => []);
        var conflicts = new List<string>();
        foreach (var entry in journal.ToArray())
        {
            var current = store.Read(entry.Path, entry.Name);
            if (current.Existed && current.Kind == RegistryValueKind.DWord && current.Value.TryGetInt32(out var number) && number == entry.Applied)
            {
                if (entry.Existed) store.Write(entry.Path, entry.Name, OriginalValue(entry), entry.Kind);
                else store.Delete(entry.Path, entry.Name);
            }
            else if (current.Existed) conflicts.Add($"Preserved an external registry change: {entry.Path}\\{entry.Name}.");
            journal.Remove(entry);
            JsonFiles.Write(journalPath, journal);
        }
        if (File.Exists(journalPath)) File.Delete(journalPath);
        return conflicts.ToArray();
    }

    private static object OriginalValue(RegistryChange entry) => entry.Kind switch
    {
        RegistryValueKind.DWord => entry.Original.GetInt32(),
        RegistryValueKind.QWord => entry.Original.GetInt64(),
        RegistryValueKind.Binary or RegistryValueKind.None => entry.Original.GetBytesFromBase64(),
        RegistryValueKind.MultiString => entry.Original.Deserialize<string[]>()!,
        _ => entry.Original.GetString()!
    };
}
