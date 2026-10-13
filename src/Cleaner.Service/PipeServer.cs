using Cleaner.Core;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Cleaner.Service;

internal static class PipeServer
{
    public static async Task Run(Engine engine, CancellationToken lifetime, string? isolatedPipeName = null)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        if (isolatedPipeName != null)
        {
            if (!isolatedPipeName.StartsWith("WindowsCleaner.Tests.", StringComparison.Ordinal))
                throw new ArgumentException("An isolated test pipe name is required.");
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        }
        // Keep this first instance alive for the service lifetime, preventing pipe-name substitution between clients.
        using var pipe = NamedPipeServerStreamAcl.Create(isolatedPipeName ?? Product.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security);
        while (!lifetime.IsCancellationRequested)
        {
            await pipe.WaitForConnectionAsync(lifetime);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                using var reader = new StreamReader(pipe, leaveOpen: true);
                var line = await PipeProtocol.ReadLineAsync(reader, timeout.Token);
                var request = JsonSerializer.Deserialize<Request>(line) ?? throw new IOException("Invalid request.");
                var admin = false;
                pipe.RunAsClient(() =>
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                });
                var response = await engine.Command(request.Command, admin, lifetime);
                await pipe.WriteAsync(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response) + "\n"), timeout.Token);
                // DisconnectNamedPipe can discard unread response bytes. Wait until the client
                // confirms consumption before reusing this instance for the next connection.
                var acknowledgment = await PipeProtocol.ReadLineAsync(reader, timeout.Token);
                if (acknowledgment != "ack") throw new IOException("Invalid response acknowledgment.");
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or UnauthorizedAccessException) { }
            finally { if (pipe.IsConnected) pipe.Disconnect(); }
        }
    }
}
