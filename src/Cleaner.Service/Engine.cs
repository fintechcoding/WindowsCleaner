using Cleaner.Core;

namespace Cleaner.Service;

internal sealed class Engine
{
    private readonly object sync = new();
    private readonly string settingsPath;
    private readonly Settings settings;
    private readonly Scheduling schedule;
    private readonly ITelemetry telemetry;
    private readonly Func<CancellationToken, CleanupResult> cleanSystem;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan tick;
    private readonly string instance = Guid.NewGuid().ToString("N");
    private CancellationTokenSource? cleanupCancellation;
    private Task cleanup = Task.CompletedTask;
    private bool busy;
    private long cycle;
    private long scheduleVersion;
    private DateTimeOffset? lastRun;
    private CleanupResult? result;
    private string? error;

    public Engine() : this(Product.DataDirectory, WindowsCleanup.System, new Telemetry(Product.DataDirectory),
        () => DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), true) { }

    internal Engine(string directory, Func<CancellationToken, CleanupResult> cleanSystem, ITelemetry telemetry,
        Func<DateTimeOffset> clock, TimeSpan tick, bool secure = false)
    {
        if (secure) SecureStorage.Ensure();
        this.cleanSystem = cleanSystem;
        this.telemetry = telemetry;
        this.clock = clock;
        this.tick = tick;
        settingsPath = Path.Combine(directory, "settings.json");
        settings = JsonFiles.Read(settingsPath, () => new Settings());
        schedule = new Scheduling(settings.Running, clock());
    }

    public Status Snapshot(string? commandError = null)
    {
        lock (sync) return new(schedule.Running, busy, settings.TelemetryBlocked, instance, cycle, lastRun,
            schedule.Next, result, commandError ?? error);
    }

    public async Task Run(CancellationToken cancellation)
    {
        if (settings.TelemetryBlocked)
        {
            lock (sync) busy = true;
            try { await Task.Run(telemetry.Enable, cancellation); }
            catch (Exception ex) { lock (sync) error = "Telemetry setup failed: " + ex.Message; }
            finally { lock (sync) busy = false; }
        }
        using var timer = new PeriodicTimer(tick);
        try
        {
            do
            {
                lock (sync)
                    if (!busy && schedule.Due(clock())) BeginCleanup(cancellation);
            } while (await timer.WaitForNextTickAsync(cancellation));
        }
        finally
        {
            lock (sync) cleanupCancellation?.Cancel();
            await cleanup;
        }
    }

    public async Task<Status> Command(string command, bool administrator, CancellationToken cancellation)
    {
        if (command == "status") return Snapshot();
        if (command is "telemetry-on" or "telemetry-off")
        {
            if (!administrator) return Snapshot("Administrator approval is required to change telemetry settings.");
            lock (sync)
            {
                if (busy) return Snapshot("A cleanup or settings operation is in progress. Please retry when it finishes.");
                busy = true;
            }
            try
            {
                if (command == "telemetry-on")
                {
                    try { await Task.Run(telemetry.Enable, cancellation); }
                    catch
                    {
                        await Task.Run(() => telemetry.Disable(), CancellationToken.None);
                        throw;
                    }
                }
                else
                {
                    var conflicts = await Task.Run(telemetry.Disable, cancellation);
                    lock (sync) error = conflicts.Length > 0 ? string.Join(Environment.NewLine, conflicts) : null;
                }
                lock (sync)
                {
                    settings.TelemetryBlocked = command == "telemetry-on";
                    if (command == "telemetry-on") error = null;
                    JsonFiles.Write(settingsPath, settings);
                }
            }
            catch (Exception ex) { lock (sync) error = "Telemetry operation failed: " + ex.Message; }
            finally { lock (sync) busy = false; }
            return Snapshot();
        }
        lock (sync)
        {
            switch (command)
            {
                case "start":
                    if (!schedule.Running) scheduleVersion++;
                    settings.Running = true;
                    JsonFiles.Write(settingsPath, settings);
                    schedule.Start(clock());
                    break;
                case "stop":
                    if (schedule.Running) scheduleVersion++;
                    settings.Running = false;
                    JsonFiles.Write(settingsPath, settings);
                    schedule.Stop();
                    cleanupCancellation?.Cancel();
                    break;
                case "clean":
                    if (!schedule.Running) return Snapshot("Cleanup is stopped. Select Start before cleaning.");
                    if (!busy) BeginCleanup(cancellation);
                    break;
                default: return Snapshot("Unknown command.");
            }
            return Snapshot();
        }
    }

    private void BeginCleanup(CancellationToken lifetime)
    {
        busy = true;
        cleanupCancellation?.Dispose();
        cleanupCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var token = cleanupCancellation.Token;
        var version = scheduleVersion;
        cleanup = Task.Run(() =>
        {
            try
            {
                var completed = cleanSystem(token);
                token.ThrowIfCancellationRequested();
                lock (sync) { result = completed; lastRun = clock(); cycle++; }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { lock (sync) error = "Cleanup failed: " + ex.Message; }
            finally { lock (sync) { busy = false; if (version == scheduleVersion) schedule.Completed(clock()); } }
        }, CancellationToken.None);
    }
}
