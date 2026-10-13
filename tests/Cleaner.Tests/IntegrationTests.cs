using Cleaner.Core;
using Cleaner.Service;
using Microsoft.Win32;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

internal static class IntegrationTests
{
    public static IEnumerable<(string Name, Action Test)> Cases()
    {
        yield return ("Engine starts automatically and does not repeat until five minutes elapse", () => WithHost(host =>
        {
            host.Start(); Until(() => host.Engine.Snapshot().Cycle == 1);
            host.Advance(TimeSpan.FromMinutes(4)); Thread.Sleep(50);
            Check(host.Calls == 1);
            host.Advance(TimeSpan.FromMinutes(1)); Until(() => host.Engine.Snapshot().Cycle == 2);
            Check(host.Calls == 2);
        }));
        yield return ("Engine coalesces concurrent manual cleanup requests", () => WithHost(host =>
        {
            using var release = new ManualResetEventSlim();
            host.Work = token => { release.Wait(token); return new(3); };
            host.Start(); Until(() => host.Calls == 1);
            Task.WhenAll(Enumerable.Range(0, 50).Select(_ => host.Engine.Command("clean", false, CancellationToken.None))).GetAwaiter().GetResult();
            Check(host.Calls == 1 && host.Engine.Snapshot().Busy);
            release.Set(); Until(() => host.Engine.Snapshot().Cycle == 1);
            Check(host.Engine.Snapshot().Result?.Deleted == 3);
        }));
        yield return ("Stop cancels active work and prevents subsequent scheduled cleanup", () => WithHost(host =>
        {
            host.Work = token => { token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); return new(); };
            host.Start(); Until(() => host.Calls == 1);
            host.Command("stop"); Until(() => !host.Engine.Snapshot().Busy);
            host.Advance(TimeSpan.FromDays(1)); Thread.Sleep(50);
            Check(!host.Engine.Snapshot().Running && host.Engine.Snapshot().Cycle == 0 && host.Calls == 1);
            var loaded = host.Recreate(); Check(!loaded.Snapshot().Running && loaded.Snapshot().NextRun == null);
            Check(host.Command("clean").Error != null);
        }));
        yield return ("Start immediately resumes after Stop during noninterruptible work", () => WithHost(host =>
        {
            using var release = new ManualResetEventSlim();
            host.Work = _ => { release.Wait(TimeSpan.FromSeconds(2)); return new(); };
            host.Start(); Until(() => host.Calls == 1);
            host.Command("stop"); host.Command("start");
            release.Set(); Until(() => host.Calls >= 2);
            Check(host.Engine.Snapshot().Running);
        }));
        yield return ("Service shutdown cancels in-flight cleanup and finishes", () => WithHost(host =>
        {
            host.Work = token => { token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); return new(); };
            host.Start(); Until(() => host.Calls == 1);
            host.Shutdown(); Check(!host.Engine.Snapshot().Busy && host.Engine.Snapshot().Cycle == 0);
        }));
        yield return ("Cleanup failures are reported and the next cycle can recover", () => WithHost(host =>
        {
            host.Work = _ => throw new IOException("Injected cleanup failure.");
            host.Start(); Until(() => !host.Engine.Snapshot().Busy && host.Calls == 1);
            Check(host.Engine.Snapshot().Error?.Contains("Injected cleanup failure") == true);
            host.Work = _ => new(2); host.Command("clean"); Until(() => host.Engine.Snapshot().Cycle == 1);
            Check(host.Engine.Snapshot().Result?.Deleted == 2);
        }));
        yield return ("Non-admin telemetry changes and unknown commands are rejected", () => WithHost(host =>
        {
            Check(host.Command("telemetry-on", false).Error != null);
            Check(host.Command("telemetry-off", false).Error != null);
            Check(host.Command("arbitrary-command", true).Error == "Unknown command.");
            Check(host.Policy.Enables == 0 && host.Policy.Disables == 0 && host.Calls == 0);
        }));
        yield return ("Telemetry toggle persists and Stop leaves blocking enabled", () => WithHost(host =>
        {
            Check(host.Command("telemetry-on", true).TelemetryBlocked);
            host.Command("stop");
            Check(host.Engine.Snapshot().TelemetryBlocked && host.Recreate().Snapshot().TelemetryBlocked);
            Check(!host.Command("telemetry-off", true).TelemetryBlocked);
            Check(!host.Recreate().Snapshot().TelemetryBlocked && host.Policy.Disables == 1);
        }));
        yield return ("Failed telemetry application rolls back and releases operation lock", () => WithHost(host =>
        {
            host.Policy.FailEnable = true;
            var response = host.Command("telemetry-on", true);
            Check(!response.TelemetryBlocked && response.Error != null && !response.Busy && host.Policy.Disables == 1);
            host.Policy.FailEnable = false;
            Check(host.Command("telemetry-on", true).TelemetryBlocked);
        }));
        yield return ("Real Windows named pipe survives malformed and sequential clients", () => WithHost(host =>
        {
            var name = "WindowsCleaner.Tests." + Guid.NewGuid().ToString("N");
            using var lifetime = new CancellationTokenSource();
            var server = PipeServer.Run(host.Engine, lifetime.Token, name);
            try
            {
                RawRequest(name, "not-json", false);
                for (var i = 0; i < 10; i++) Check(RawRequest(name, JsonSerializer.Serialize(new Request("status")), true)!.Running);
                Check(RawRequest(name, JsonSerializer.Serialize(new Request("stop")), true)!.Running == false);
                Check(RawRequest(name, JsonSerializer.Serialize(new Request("clean")), true)!.Error != null);
                Check(host.Calls == 0);
            }
            finally { lifetime.Cancel(); AwaitCancellation(server); }
        }));
        yield return ("Registry rollback survives a write interruption", () => WithDirectory(root =>
        {
            var store = new FaultingRegistry();
            store.Write("Test", "Value", "original", RegistryValueKind.ExpandString);
            var journal = new RegistryJournal(store, Path.Combine(root, "journal.json"));
            store.Fail = true;
            try { journal.Apply([("Test", "Value", 1)]); throw new InvalidOperationException("Failure was not injected."); }
            catch (IOException) { }
            Check(File.Exists(Path.Combine(root, "journal.json")));
            store.Fail = false;
            new RegistryJournal(store, Path.Combine(root, "journal.json")).Apply([("Test", "Value", 1)]);
            journal.Restore();
            Check(store.Read("Test", "Value").Value.GetString() == "original");
            Check(store.Read("Test", "Value").Kind == RegistryValueKind.ExpandString);
        }));
        yield return ("Rollback restores DWORD, QWORD, binary and multi-string types", () => WithDirectory(root =>
        {
            var store = new FaultingRegistry();
            store.Write("Test", "Dword", 9, RegistryValueKind.DWord);
            store.Write("Test", "Qword", 9000000000L, RegistryValueKind.QWord);
            store.Write("Test", "Binary", new byte[] { 1, 2, 255 }, RegistryValueKind.Binary);
            store.Write("Test", "Multi", new string[] { "first", "second" }, RegistryValueKind.MultiString);
            var journal = new RegistryJournal(store, Path.Combine(root, "journal.json"));
            journal.Apply([("Test", "Dword", 1), ("Test", "Qword", 1), ("Test", "Binary", 1), ("Test", "Multi", 1)]);
            journal.Restore();
            Check(store.Read("Test", "Dword").Value.GetInt32() == 9);
            Check(store.Read("Test", "Qword").Value.GetInt64() == 9000000000L);
            Check(store.Read("Test", "Binary").Value.GetBytesFromBase64().SequenceEqual(new byte[] { 1, 2, 255 }));
            Check(store.Read("Test", "Multi").Value.Deserialize<string[]>()!.SequenceEqual(new[] { "first", "second" }));
        }));
        yield return ("Windows native API entry points are available without invoking cleanup", () =>
        {
            foreach (var (library, names) in new[] {
                ("wevtapi.dll", new[] { "EvtOpenChannelEnum", "EvtNextChannelPath", "EvtClearLog" }),
                ("dnsapi.dll", new[] { "DnsFlushResolverCache" }),
                ("advapi32.dll", new[] { "RegisterServiceCtrlHandlerExW", "QueryServiceStatusEx", "SetServiceStatus" }) })
            {
                var handle = NativeLibrary.Load(library);
                try { foreach (var name in names) Check(NativeLibrary.TryGetExport(handle, name, out _)); }
                finally { NativeLibrary.Free(handle); }
            }
        });
    }

    private static Status? RawRequest(string name, string message, bool expectResponse)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Impersonation);
        pipe.Connect(2000);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        pipe.Write(System.Text.Encoding.UTF8.GetBytes(message + "\n"));
        string? response;
        try { response = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
        catch (IOException) when (!expectResponse) { return null; }
        if (expectResponse) pipe.Write("ack\n"u8);
        return expectResponse ? JsonSerializer.Deserialize<Status>(response!)! : null;
    }
    private static void WithHost(Action<Host> test) => WithDirectory(root => { using var host = new Host(root); test(host); });
    private static void WithDirectory(Action<string> test)
    {
        var root = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try { test(root); }
        finally { Check(!SafeCleanup.HasLinkedAncestor(root)); Directory.Delete(root, true); }
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
    private static void Until(Func<bool> predicate)
    {
        var end = Environment.TickCount64 + 3000;
        while (!predicate()) { if (Environment.TickCount64 > end) throw new TimeoutException("Expected state was not reached."); Thread.Sleep(5); }
    }
    private static void AwaitCancellation(Task task)
    {
        try { task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
    }
    private sealed class Host : IDisposable
    {
        private readonly string directory;
        private readonly CancellationTokenSource lifetime = new();
        private Task? run;
        private long ticks = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero).Ticks;
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public readonly FakePolicy Policy = new();
        public Func<CancellationToken, CleanupResult> Work = _ => new(1);
        public Engine Engine { get; }
        public Host(string directory) { this.directory = directory; Engine = Recreate(); }
        public Engine Recreate() => new(directory, token => { Interlocked.Increment(ref calls); return Work(token); }, Policy,
            () => new DateTimeOffset(Interlocked.Read(ref ticks), TimeSpan.Zero), TimeSpan.FromMilliseconds(10));
        public void Start() { run = Engine.Run(lifetime.Token); }
        public void Advance(TimeSpan time) { Interlocked.Add(ref ticks, time.Ticks); }
        public Status Command(string command, bool admin = false) => Engine.Command(command, admin, lifetime.Token).GetAwaiter().GetResult();
        public void Shutdown() { lifetime.Cancel(); if (run != null) AwaitCancellation(run); }
        public void Dispose() { Shutdown(); lifetime.Dispose(); }
    }
    private sealed class FakePolicy : ITelemetry
    {
        public int Enables, Disables;
        public bool FailEnable;
        public void Enable() { Enables++; if (FailEnable) throw new IOException("Injected policy failure."); }
        public string[] Disable() { Disables++; return []; }
    }
    private sealed class FaultingRegistry : IRegistryStore
    {
        private readonly MemoryRegistry store = new();
        public bool Fail;
        public RegistrySnapshot Read(string path, string name) => store.Read(path, name);
        public void Write(string path, string name, object value, RegistryValueKind kind)
        { if (Fail) throw new IOException("Injected write interruption."); store.Write(path, name, value, kind); }
        public void Delete(string path, string name) => store.Delete(path, name);
    }
}
