# Standalone 0.102.2933.4

Based on Microsoft PowerToys release `v0.102.2933.0`, commit
`9673c84ffac3bcc55e75fbecaedcda9e7d698530` (MIT licensed).
This is an independent, unsigned build, not an official Microsoft release.

- Publish only the Mouse Without Borders WinForms application and its clipboard helper,
  using a shared self-contained .NET 10 Windows x64 runtime.
- Replace the PowerToys settings library with compatible configuration models and a small
  local adapter. Store settings under `MouseWithoutBordersStandalone`, write atomically,
  and preserve the preceding file as `settings.json.bak`.
- Save synchronously to prevent state loss on exit and out-of-order background writes.
- Replace the C++ GPO wrapper with managed registry reads using the same policy names,
  machine-first precedence, and global enablement fallback.
- Remove runner lifetime coupling, telemetry calls, Command Palette listeners, and the
  startup call to the PowerToys settings IPC server. Settings are edited in-process.
- Disable service-mode startup and configuration. User desktop operation is the supported
  deployment scope. Upstream service/security code retained in source is not activated.
- Restore the first-computer YES/NO setup page and enable the standalone tray UI by default.
  Replace obsolete shortcut widgets with a summary of active shortcuts and Easy Mouse mode.
- Isolate clipboard IPC by user SID and session. Restrict the pipe to the current user,
  honor cancellation while connected, and bound helper connect attempts to five seconds.
- Rename the clipboard helper and its window. Exit the helper when its parent exits.
- Use High process priority instead of RealTime. Report fatal startup failures in a dialog.
- Use local logs instead of registering Windows Event Log sources.
- Update MessagePack from upstream 3.1.7 to 3.1.12 to resolve NuGet's vulnerability advisory.

The TCP input protocol, encryption, mouse/keyboard hooks, switching logic and native input
injection are retained from the tagged upstream source. No cryptographic downgrade was made.
The original non-service pairing-key persistence behavior is retained.

Validation includes upstream encryption, key codec, file path validation and IPC tests,
plus standalone defaults, immediate persistence/reload, malformed configuration handling,
encrypted data over a real loopback TCP socket, and repeated clipboard IPC reconnection.
One upstream test of private .NET implementation details is intentionally ignored upstream.
Physical two-computer and multi-monitor validation remains outstanding.

Chinese edition: localize the pairing wizard, settings, tray menus, connection status and common dialogs. Draw Chinese captions for the wizard image buttons; preserve stored Easy Mouse enum values. Persist changes made in Other Options immediately. Verified Chinese layout and option persistence in the running final build.

Custom pairing-code edition: make the first-computer code editable with explicit Save;
accept custom codes of at least 16 non-whitespace characters, retaining the existing
encrypted transport. Apply the same save path to settings and peer setup, preserve
case-only edits, and prevent the legacy generated-key flag from rejecting a validated
custom code. Increase input fields to 256 characters. Add persistence/reload,
invalid-input and case-change regression coverage.

Connection-memory edition (0.102.2933.4): persist authenticated peer names, IDs,
layout and first-run completion on both inbound and outbound connections, without
waiting for the user to finish the wizard. Preserve existing pairing codes and
computer arrangements when reopening setup. Existing saved names drive the upstream
automatic reconnect loop. Clipboard text/image sharing remains enabled by default;
persist its toggle immediately while respecting existing opt-out and organization policy.
Regression coverage includes first-run key reuse, both connection directions,
layout preservation, clipboard preferences, and Unicode text/PNG bytes across the
real helper IPC transport, including cancellation and reconnection. No physical
two-computer clipboard or automatic reconnect test was performed for this build.
