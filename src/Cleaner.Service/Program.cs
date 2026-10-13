using Cleaner.Core;
using Cleaner.Service;
using System.Security.Principal;

English.Initialize();

if (args.Length != 0)
{
    var administrator = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    if (!administrator) return 5;
    try
    {
        if (args is ["--initialize"])
        {
            SecureStorage.Ensure();
            var path = Path.Combine(Product.DataDirectory, "settings.json");
            if (!File.Exists(path)) JsonFiles.Write(path, new Settings { TelemetryBlocked = true });
            return 0;
        }
        if (args is ["--restore"])
        {
            SecureStorage.Ensure();
            new Telemetry(Product.DataDirectory).Disable();
            var path = Path.Combine(Product.DataDirectory, "settings.json");
            var settings = JsonFiles.Read(path, () => new Settings());
            settings.TelemetryBlocked = false;
            JsonFiles.Write(path, settings);
            return 0;
        }
        if (args is ["--reset"])
        {
            SecureStorage.Ensure();
            new Telemetry(Product.DataDirectory).Disable();
            JsonFiles.Write(Path.Combine(Product.DataDirectory, "settings.json"), new Settings { Running = false });
            return 0;
        }
        return 87;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}

return NativeService.Run(async lifetime =>
{
    var engine = new Engine();
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    var scheduler = engine.Run(linked.Token);
    var server = PipeServer.Run(engine, linked.Token);
    await Task.WhenAny(scheduler, server);
    linked.Cancel();
    try { await Task.WhenAll(scheduler, server); }
    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
});
