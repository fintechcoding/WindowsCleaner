# Windows Cleaner

An English-only Windows 10 22H2 / Windows 11 x64 tray application and maintenance service. No application file logs or application telemetry are produced. Settings and rollback journals are retained; Windows itself continues to record events.

## Install and use

Run `artifacts/installer/WindowsCleaner-Setup-1.0.0.exe` as administrator. Installation starts cleanup immediately. Open **Windows Cleaner** from the Start menu if the tray icon is not visible. The runtime is bundled; no separate .NET installation is required.

- **Start / Stop:** enable or pause cleanup. The choice survives reboot. Stop cancels remaining cleanup work but cannot undo items already deleted.
- **Clean now:** request a cleanup while running.
- **Status:** display the latest system and current-user results in memory.
- **Enable / Disable telemetry blocking:** change machine privacy and network settings through a separate administrator approval prompt. Stop does not undo these settings.
- **Exit tray:** close the user agent; system cleanup continues. Current-user cleanup resumes when the tray is reopened. User privacy preferences are restored on tray exit and reapplied on the next connection if blocking is enabled.

The service runs at boot under LocalSystem. The tray runs at user logon. Only accounts running the tray have their user data cleaned; no other profiles are scanned. Local interactive users can start, stop and request the configured cleanup. Only administrators can change machine telemetry settings.

## Cleanup boundaries

- Current user's standard `mstsc` bitmap cache, MRU and server history, default connection file and `.rdp` recent-document entries. Saved credentials and manually saved `.rdp` files are preserved.
- Current-user temp and `%SystemRoot%\Temp`, with no age limit. Locked/read-only/inaccessible files and linked entries are skipped. Directory handles prevent junction replacement during recursive cleanup. Root directories are preserved.
- All enumerated Windows Event Log channels are cleared using the supported API when permissions and channel state allow it. Security clearing creates event 1102. Event logging is not disabled; existing forwarded or remote logs cannot be removed.
- System DNS resolver cache. Active TCP connections, browser-private caches, setup logs and ETL trace files are not cleared.

The five-minute interval starts after each system cleanup completes; overlapping and catch-up runs are prevented. Current-user cleanup follows the completed system cycle while the tray is running. A user cleanup that outlasts a cycle is never overlapped.

## Telemetry and rollback

Machine policies reduce diagnostic data to the lowest applicable edition level and suppress feedback notifications. User policies disable diagnostic-data-based personalization while the tray is connected. A service-specific Windows Firewall rule blocks DiagTrack outbound traffic. An application-owned hosts block redirects a fixed list of documented diagnostics / error-reporting domains to IPv4 and IPv6 unspecified addresses. It does not block all Microsoft domains or shared CDN IP addresses; hosts blocking does not cover clients that bypass the system resolver.

Windows Update, Defender, sign-in and optional Microsoft features must be checked on the target machine. A guarantee of zero Microsoft traffic is not possible.

Rollback captures registry values before mutation, preserves external registry changes, removes only the owned hosts block and owned firewall rule, and retains unrelated hosts entries. Machine rollback information is stored in the administrator/System-only `%ProgramData%\WindowsCleaner` directory; user rollback information is in `%LocalAppData%\WindowsCleaner`. These are configuration journals, not activity logs.

Uninstall through **Settings > Apps > Windows Cleaner**. The uninstaller stops the service, restores managed machine settings and removes startup entries. Close tray agents in other logged-in sessions before uninstalling/upgrading. Their user preferences are restored on exit or when the service becomes unavailable. If a tray was forcibly killed, run `WindowsCleaner.exe --restore-user` under that account before removing the installation. The small running uninstaller is removed on reboot. Machine settings are retained for recovery/reinstallation.

## Build and test

Requirements: .NET 10 SDK, installed official .NET 10 x64 core and desktop runtimes, and the Windows .NET Framework compiler. No third-party NuGet dependencies are used.

```powershell
./scripts/build.ps1
```

The build isolates CLI configuration in `artifacts`, compiles all projects with warnings treated as errors, runs the safe tests, publishes DLLs, bundles the official installed runtime and generates an offline installer using the Windows .NET Framework compiler. An Inno Setup recipe is also provided; use `./scripts/build.ps1 -InnoCompiler 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'` to build that installer instead.

The offline installer uses Windows' existing .NET Framework UI/bootstrap support. The application and service use the bundled .NET 10 runtime. Both installers are unsigned.

For a read-only UI preview before installation, run `artifacts/payload/WindowsCleaner.exe --preview`. Preview never connects to the service or changes privacy settings and does not clean any files.

The 21 safe tests mutate only temporary directories inside `artifacts/tests` and use an isolated Windows named pipe. Registry rollback and machine operations are tested through isolated implementations. They never clear real Windows event logs, flush DNS, modify hosts/firewall, or delete real temp/RDP data. See [verification results](docs/TEST_RESULTS.md) and [the manual acceptance checklist](docs/TESTING.md) for remaining VM-only checks.

## References

- [Microsoft diagnostic data policies and endpoints](https://learn.microsoft.com/en-us/windows/privacy/configure-windows-diagnostic-data-in-your-organization)
- [Microsoft experience privacy policies](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience)
- [Event 1102: audit log cleared](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/auditing/event-1102)
- [Windows services and user interface isolation](https://learn.microsoft.com/en-us/windows/win32/services/interactive-services)
