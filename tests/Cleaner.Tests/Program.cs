using Cleaner.Core;
using Microsoft.Win32;

var tests = new (string Name, Action Test)[]
{
    ("Scheduler starts immediately and waits five minutes after completion", () =>
    {
        var now = DateTimeOffset.UtcNow;
        var schedule = new Scheduling(true, now);
        Check(schedule.Due(now));
        schedule.Completed(now);
        Check(!schedule.Due(now.AddMinutes(4)) && schedule.Due(now.AddMinutes(5)));
        schedule.Completed(now.AddHours(1));
        Check(!schedule.Due(now.AddHours(1)) && schedule.Next == now.AddHours(1).AddMinutes(5));
        schedule.Stop(); Check(!schedule.Due(now.AddDays(10)) && schedule.Next == null);
        schedule.Start(now); Check(schedule.Due(now));
    }),
    ("Hosts edits preserve unrelated entries and are idempotent", () =>
    {
        const string original = "127.0.0.1 localhost\r\n192.0.2.1 internal.example\r\n";
        var blocked = HostsBlock.Apply(original);
        Check(blocked.Contains("0.0.0.0 v10.events.data.microsoft.com"));
        Check(blocked.Contains(":: v10.events.data.microsoft.com"));
        Check(HostsBlock.Apply(blocked) == blocked);
        Check(HostsBlock.Remove(blocked) == original);
        Check(HostsBlock.Remove(blocked + "192.0.2.2 later.example\r\n") == original + "192.0.2.2 later.example\r\n");
        Throws<InvalidDataException>(() => HostsBlock.Remove(HostsBlock.Begin));
    }),
    ("Cleanup preserves root, skips locked files, and removes recent files", () => InTemporaryDirectory(root =>
    {
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        File.WriteAllText(Path.Combine(root, "nested", "new.txt"), "temporary");
        var locked = Path.Combine(root, "locked.txt");
        using var held = new FileStream(locked, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var result = SafeCleanup.DirectoryContents(root);
        Check(result.Deleted == 1 && result.Skipped >= 1);
        Check(Directory.Exists(root) && File.Exists(locked) && !Directory.Exists(Path.Combine(root, "nested")));
    })),
    ("Cleanup does not follow a junction outside the root", () => InTemporaryDirectory(root =>
    {
        var outside = Path.Combine(root, "outside");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(outside); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        var junction = Path.Combine(target, "junction");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe", Arguments = $"/d /c mklink /J \"{junction}\" \"{outside}\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        process.WaitForExit(); Check(process.ExitCode == 0);
        var result = SafeCleanup.DirectoryContents(target);
        Check(result.Skipped >= 1 && File.Exists(Path.Combine(outside, "keep.txt")));
        Check(SafeCleanup.DirectoryContents(junction).Deleted == 0);
        Directory.Delete(junction); // Delete only the junction, never recursively.
    })),
    ("Cleanup cancellation prevents deletion", () => InTemporaryDirectory(root =>
    {
        File.WriteAllText(Path.Combine(root, "keep.txt"), "keep");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Throws<OperationCanceledException>(() => SafeCleanup.DirectoryContents(root, cancellation.Token));
        Check(File.Exists(Path.Combine(root, "keep.txt")));
    })),
    ("Settings preserve Stop across a restart", () => InTemporaryDirectory(root =>
    {
        var path = Path.Combine(root, "settings.json");
        JsonFiles.Write(path, new Settings { Running = false, TelemetryBlocked = true });
        var loaded = JsonFiles.Read(path, () => new Settings());
        Check(!loaded.Running && loaded.TelemetryBlocked);
    })),
    ("Registry journal restores original types and preserves external changes", () => InTemporaryDirectory(root =>
    {
        const string path = "TestPolicies";
        var store = new MemoryRegistry();
            store.Write(path, "Original", "prior", RegistryValueKind.String);
            var journal = new RegistryJournal(store, Path.Combine(root, "rollback.json"));
            journal.Apply([(path, "Original", 1), (path, "Absent", 1), (path, "Changed", 1)]);
            journal.Apply([(path, "Original", 1)]); // Reapply must not replace the captured original.
            store.Write(path, "Changed", 7, RegistryValueKind.DWord);
            var conflicts = journal.Restore();
            Check(store.Read(path, "Original").Value.GetString() == "prior" && store.Read(path, "Original").Kind == RegistryValueKind.String);
            Check(!store.Read(path, "Absent").Existed && store.Read(path, "Changed").Value.GetInt32() == 7 && conflicts.Length == 1);
            Check(!File.Exists(Path.Combine(root, "rollback.json")));
    })),
    ("Protocol rejects oversized input", () =>
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(new string('x', 32769) + "\n"));
        using var reader = new StreamReader(stream);
        Throws<IOException>(() => PipeProtocol.ReadLineAsync(reader, CancellationToken.None).GetAwaiter().GetResult());
    })
};

var failures = 0;
var allTests = tests.Concat(IntegrationTests.Cases()).ToArray();
foreach (var (name, test) in allTests)
{
    try { test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
Console.WriteLine($"{allTests.Length - failures}/{allTests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
static void InTemporaryDirectory(Action<string> test)
{
    // All filesystem mutations stay within the project workspace.
    var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try { test(root); }
    finally { if (!SafeCleanup.HasLinkedAncestor(root)) Directory.Delete(root, true); }
}

sealed class MemoryRegistry : IRegistryStore
{
    private readonly Dictionary<(string, string), RegistrySnapshot> values = [];
    public RegistrySnapshot Read(string path, string name) => values.GetValueOrDefault((path, name)) ??
        new(false, RegistryValueKind.DWord, System.Text.Json.JsonSerializer.SerializeToElement<object?>(null));
    public void Write(string path, string name, object value, RegistryValueKind kind) =>
        values[(path, name)] = new(true, kind, System.Text.Json.JsonSerializer.SerializeToElement(value));
    public void Delete(string path, string name) => values.Remove((path, name));
}
