using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;

namespace Cleaner.Core;

public static class HostsBlock
{
    public const string Begin = "# BEGIN WindowsCleaner telemetry";
    public const string End = "# END WindowsCleaner telemetry";
    public static readonly string[] Endpoints =
    [
        "v10.events.data.microsoft.com", "v10c.events.data.microsoft.com", "v10.vortex-win.data.microsoft.com",
        "self.events.data.microsoft.com", "watson.telemetry.microsoft.com", "watson.events.data.microsoft.com",
        "umwatsonc.events.data.microsoft.com", "ceuswatcab01.blob.core.windows.net", "ceuswatcab02.blob.core.windows.net",
        "eaus2watcab01.blob.core.windows.net"
    ];

    public static string Remove(string text)
    {
        var start = text.IndexOf(Begin, StringComparison.Ordinal);
        if (start < 0)
        {
            if (text.Contains(End, StringComparison.Ordinal)) throw new InvalidDataException("Incomplete hosts block.");
            return text;
        }
        var end = text.IndexOf(End, start, StringComparison.Ordinal);
        if (end < 0 || text.IndexOf(Begin, start + Begin.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("Incomplete or duplicate hosts block.");
        end += End.Length;
        if (text.AsSpan(end).StartsWith("\r\n")) end += 2;
        else if (end < text.Length && text[end] == '\n') end++;
        return text.Remove(start, end - start);
    }

    public static string Apply(string text)
    {
        var clean = Remove(text);
        if (clean.Length > 0 && !clean.EndsWith('\n')) clean += "\r\n";
        return clean + Begin + "\r\n" + string.Join("\r\n", Endpoints.SelectMany(host => new[] { $"0.0.0.0 {host}", $":: {host}" })) + "\r\n" + End + "\r\n";
    }
}

public sealed class Telemetry(string dataDirectory) : ITelemetry
{
    private const string RuleName = "WindowsCleaner.DiagTrack.Outbound.v1";
    private const string RuleGroup = "WindowsCleaner";
    private string StatePath => Path.Combine(dataDirectory, "telemetry.json");
    private RegistryJournal Journal => new(Registry.LocalMachine, Path.Combine(dataDirectory, "registry-rollback.json"));
    public sealed class State { public bool HostsOwned { get; set; } public bool FirewallOwned { get; set; } }

    public void Enable()
    {
        var state = JsonFiles.Read(StatePath, () => new State());
        var edition = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")?.GetValue("EditionID") as string ?? "";
        var level = edition.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) || edition.Contains("Education", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        Journal.Apply([
            (@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", level),
            (@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications", 1)
        ]);
        EditHosts(text =>
        {
            if (!state.HostsOwned && text.Contains(HostsBlock.Begin, StringComparison.Ordinal))
                throw new InvalidDataException("An unmanaged WindowsCleaner hosts block already exists.");
            state.HostsOwned = true;
            JsonFiles.Write(StatePath, state);
            return HostsBlock.Apply(text);
        });
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        dynamic? existing = FindRule(policy);
        if (existing != null && (!state.FirewallOwned || !OwnedRule(existing)))
            throw new InvalidOperationException("A conflicting firewall rule already exists.");
        if (existing == null)
        {
            dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
            rule.Name = RuleName;
            rule.Description = "Blocks outgoing traffic for the Connected User Experiences and Telemetry service.";
            rule.Grouping = RuleGroup;
            rule.ApplicationName = Path.Combine(Environment.SystemDirectory, "svchost.exe");
            rule.ServiceName = "DiagTrack";
            rule.Direction = 2;
            rule.Action = 0;
            rule.Profiles = int.MaxValue;
            rule.Enabled = true;
            state.FirewallOwned = true;
            JsonFiles.Write(StatePath, state);
            policy.Rules.Add(rule);
        }
        FlushDns();
    }

    public string[] Disable()
    {
        var state = JsonFiles.Read(StatePath, () => new State());
        if (state.FirewallOwned)
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            dynamic? rule = FindRule(policy);
            if (rule != null)
            {
                if (!OwnedRule(rule)) throw new InvalidOperationException("The firewall rule was modified externally; restore it manually before retrying.");
                policy.Rules.Remove(RuleName);
            }
            state.FirewallOwned = false;
            JsonFiles.Write(StatePath, state);
        }
        if (state.HostsOwned)
        {
            EditHosts(HostsBlock.Remove);
            state.HostsOwned = false;
            JsonFiles.Write(StatePath, state);
        }
        var conflicts = Journal.Restore();
        if (File.Exists(StatePath)) File.Delete(StatePath);
        FlushDns();
        return conflicts;
    }

    private static dynamic? FindRule(dynamic policy)
    {
        try { return policy.Rules.Item(RuleName); }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { return null; }
    }

    private static bool OwnedRule(dynamic rule) => rule.Grouping == RuleGroup && rule.ServiceName == "DiagTrack"
        && rule.ApplicationName.Equals(Path.Combine(Environment.SystemDirectory, "svchost.exe"), StringComparison.OrdinalIgnoreCase)
        && rule.Direction == 2 && rule.Action == 0;

    private static void EditHosts(Func<string, string> edit)
    {
        var path = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
        if (SafeCleanup.HasLinkedAncestor(Path.GetDirectoryName(path)!) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("A linked hosts file cannot be edited.");
        // Exclusive open prevents lost updates from another editor during this operation.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true);
        var original = reader.ReadToEnd();
        var encoding = reader.CurrentEncoding;
        var updated = edit(original);
        if (updated == original) return;
        var content = encoding.GetBytes(updated);
        var preamble = encoding.GetPreamble();
        stream.Position = 0;
        stream.Write(preamble);
        stream.Write(content);
        stream.SetLength(stream.Position);
        stream.Flush(true);
    }

    [DllImport("dnsapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DnsFlushResolverCache();
    private static void FlushDns() => DnsFlushResolverCache();
}
