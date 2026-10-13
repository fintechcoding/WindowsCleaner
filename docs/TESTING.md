# Manual acceptance checklist

Run destructive checks in disposable Windows 10 22H2 and Windows 11 x64 VMs with a snapshot. Do not start the service on a development workstation to validate cleanup.

1. Install the unsigned setup as administrator. Confirm the service is automatic and running, the tray is present, and the first system/user cleanup completes. Confirm the product is listed in Apps and the Start menu.
2. Create ordinary files in current-user temp and Windows temp, including a nested directory, a locked file and a junction to a separate directory containing a sentinel. Request Clean now. Ordinary files should be removed; the root, locked file, linked entry and external sentinel should survive. Do not force-close processes.
3. Connect with standard mstsc; establish history and bitmap cache. Clean while connected. Confirm the connection continues, removable cache/history is cleared, locked cache is reported, saved credentials and manually saved connection files remain. Repeat after closing mstsc.
4. Seed a harmless application event and perform cleanup. Check clearing results for Application/System/Security and operational channels; failures should not stop other cleanup. Confirm Security contains a newly generated clear event. Do not expect the whole machine to have zero events.
5. Populate system DNS cache, request cleanup and inspect the resolver cache promptly. Confirm active TCP connections remain established. Other programs may repopulate DNS immediately.
6. Observe two scheduled runs at least five minutes apart. Sleep/resume the VM; confirm no burst of catch-up runs. Select Stop during cleanup and confirm remaining work stops. Reboot; the service and tray start but cleanup remains paused. Select Start and confirm it resumes.
7. Before installing, capture machine diagnostic policy values, user privacy values, hosts content and firewall rules in the VM test harness. Check the managed hosts entries and DiagTrack rule after installation; test IPv4 and IPv6 resolution for an exact blocked domain. Verify Windows Update, Defender updates and Microsoft-account sign-in. Packet capture is an external test tool and is not part of the product.
8. Disable blocking. Confirm original machine/user values return, unrelated hosts entries survive and only the owned firewall rule disappears. Add an unrelated hosts entry while blocking is enabled; it must survive rollback. Change a managed registry value externally; rollback should preserve the external value.
9. As a standard interactive user, verify Start/Stop work and the telemetry toggle requests administrator approval. Confirm direct non-admin telemetry commands are rejected. Remote network access to the named pipe must be denied.
10. Install an update over the same installation with cleanup stopped. Confirm settings survive and tray/service restart. Close agents in other sessions first. Uninstall and verify machine rollback, user rollback, service removal, startup removal and Apps removal. Reboot to remove the uninstaller stub.
11. Inspect the application directories for activity logs: none should exist. Settings, rollback journals, runtime licensing files and Windows-generated system events are expected.

## Automated verification limits

Unit/safe filesystem tests cover scheduling, cancellation, locked files, junction boundaries, settings persistence, registry rollback and hosts preservation. The repository does not claim VM acceptance checks have passed merely because compilation and these safe tests pass.
