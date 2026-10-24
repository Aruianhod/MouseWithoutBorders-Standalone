# Standalone 0.102.2933.10 — optional elevated logon startup

- Add a reversible startup checkbox backed by a current-user interactive scheduled task.
  Use highest available privileges, a five-second logon delay, no stored password or boot trigger,
  and no battery/long-session termination. Verify registration and preserve foreign tasks.
- Show actual registered task state, support refreshing a moved installation path, and keep
  in-place upgrades compatible. No service or login-screen control is added.
- Validate generated task XML against the real Windows Task Scheduler without registering in tests.
- Clarify the two audio setup roles and the need for a working mouse connection before audio testing.

# Standalone 0.102.2933.9 — default administrator startup and stale audio endpoints

- Request same-account administrator rights before initializing input, settings or audio.
  Keep an asInvoker bootstrap so older CreateProcess-based updaters can start this release.
  Consent cancellation exits the bootstrap without initializing the application.
- Skip NotPresent audio endpoints before reading their property stores during VB-CABLE detection.
  Preserve checks for disabled/unplugged cables, stage-by-stage progress and full failure logs.
- Add regression coverage for missing device property stores and contextual failure messages.
- The remote screenshot's exact failure stack and physical driver/audio flow remain unverified.

# Standalone 0.102.2933.8 — dialog ownership and administrator restart

- Remove always-on-top from settings and pairing windows. Give tray dialogs a visible owner,
  and preserve nested audio setup ownership; never use the hidden input overlay as owner.
- Add explicit administrator restart to tray and software update settings using Windows runas.
  Keep the original process on consent cancellation, failed launch or readiness timeout.
  Validate same user, parent process path/start time and wait for parent exit before initialization.
- Preserve pairing/settings. No service mode, secure desktop support or automatic elevation.
- Add native modal ownership tests and elevation handoff readiness/failure tests.

# Standalone 0.102.2933.7 — shared-folder updates and file drop window

- Add pinned RSA-signed LAN update feed, bounded downloads, ZIP and version validation,
  separate self-contained updater, pre-exit readiness handshake, retained old directory,
  and rollback on activation/launch-call failure. Auto-check only notifies; restart requires confirmation.
- Keep update private key outside the source tree, encrypted for the build user's Windows account.
- Add a multi-file drop window with cancellation, streaming SHA-256 verification and receiver
  acknowledgement over the existing authenticated encrypted clipboard socket. Limit each file to 10 GiB.
- Never overwrite received files; reject unsafe names and clean unfinished staging files.
- Retain native cross-screen single-file dragging, correct pointer-sized window handles and
  report unsupported multi-selection/folders instead of silently selecting the first file.
- Physical two-computer update restart and file dragging remain unverified. Audio remains experimental.

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

Audio preview (0.102.2933.5): add independent opt-in system-loopback and microphone
send/receive routes, endpoint selection, remembered peers, per-route volume, bounded
buffering, configurable audio port, status and a stop-all action. Add Chinese settings
from the tray and main settings window. NAudio.Wasapi/Core 2.2.1 provide shared-mode
48 kHz float capture and playback. Transport uses separate TLS TCP connections and
mutual pairing-secret proofs bound to the server certificate, fresh nonces and stream
metadata. Require a current authenticated mouse connection and the configured peer;
start capture only after the receiver authenticates and prepares its output device.
Disconnect/key changes stop the channel; device/network failures retry. Receivers
are capped at one stream of each kind, with bounded handshakes and frame sizes.
Prevent receiving into the same endpoint being captured for system-audio sharing.

Microphone injection into arbitrary remote applications requires a separately
installed virtual audio endpoint; select its render endpoint here and its capture
endpoint in the remote application. No driver, trust-store certificate, firewall
rule or default audio device is installed/changed by this build. Windows Schannel
requires a temporary user-scoped certificate key container (not EphemeralKeySet).
No user audio was recorded during verification; synthetic PCM tests use real local
TCP/TLS. Physical audio capture/playback, driver routing, latency, and two-computer
operation remain unverified. This is an experimental feature, not production audio QA.

One-click audio setup (0.102.2933.6): choose a paired peer and headset/remote role to
configure complementary audio routes. Detect the base VB-CABLE pair; if missing,
verify and extract the bundled original Pack45 and invoke its signed installer with
Windows elevation. Keep vendor/system prompts visible. Persist defaults and route
backups before installation; if endpoints are not ready, persist a pending plan and
register a one-time current-user startup to resume after reboot. Do not reboot,
uninstall existing drivers, install trust-store certificates or change firewall rules.
Offer switching all capture-default roles to the remote microphone and restoring
prior settings while preserving subsequent manual device changes. Default switching
uses the internal Windows PolicyConfig COM interface and verifies results. The
interface's availability was tested; actual default switching and driver installation
were deliberately not executed. VB-CABLE remains under its separate donationware
license; see the origin, licensing and support links in the UI and Drivers notice.
