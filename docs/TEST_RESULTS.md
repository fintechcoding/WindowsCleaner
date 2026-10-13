# Verification results

## Passed

- Release build: zero warnings, zero errors.
- Automated suite: 21 of 21 tests passed.
- Scheduling: immediate start, five-minute spacing, pause persistence, cancellation, immediate restart after Stop, service shutdown, and recovery after a failed cleanup.
- Concurrency: 50 overlapping manual requests produce one cleanup operation.
- Real Windows filesystem: nested and newly created files are removed; locked files, root directories and junction targets are preserved.
- Real Windows named pipe transport: malformed input does not terminate the server; sequential clients receive responses; Stop and stopped-state rejection work with isolated operations.
- Authorization behavior: non-admin telemetry changes and unknown commands are rejected before operations execute.
- Rollback: external registry changes are preserved, interrupted writes recover, and DWORD/QWORD/binary/multi-string/expand-string originals are restored using a test registry store.
- Hosts transformations: unrelated entries are preserved, IPv4/IPv6 managed entries are generated, repeated application is idempotent, incomplete blocks are rejected.
- Bundled runtime loads without using a separately installed runtime.
- Preview window launches and shuts down with exit code 0 following the double-disposal fix.
- Installer resource contains the published application and official runtime files.

## Fixes discovered through verification

1. Tray disposal is idempotent, eliminating the reported managed exception during shutdown.
2. Named pipe responses are acknowledged before disconnect, preventing unread response loss. Transport writes avoid stream-writer disposal against an already disconnected pipe.
3. A Stop followed immediately by Start preserves the immediate schedule instead of delaying the restart by five minutes.
4. Canceled cleanup does not publish a completed cycle.

## Not verified in this environment

Full administrator installation/uninstallation, service control through SCM, boot/login startup, real event-log clearing, actual mstsc cache/history removal, DNS flushing, firewall enforcement, and Windows Update/Defender/sign-in compatibility still require the disposable Windows 10/11 VM acceptance checks in TESTING.md. Test cleanup and policy operations are isolated; these results do not claim a full-machine destructive test has run.

The OS API exports are present, but export availability alone is not proof that their privileged operations succeed. Registry policy operations are exercised through an in-memory implementation because this execution environment restricts Registry writes.
