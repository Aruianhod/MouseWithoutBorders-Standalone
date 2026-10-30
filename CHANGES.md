# Standalone 0.102.2933.23 — point-locked game cursor detection

- Accept zero-area point/line cursor constraints inside the foreground application's client rectangle. Receiver-side diagnostics reproduced Zenless Zone Zero hiding the cursor and reporting a point clip; rejecting zero width/height prevented automatic relative mode from activating.
- Recheck capture on the injection thread and discard queued absolute mouse moves after the game acquires capture, including centering moves. Relative input and button/key events remain unchanged.
- Preserve foreground eligibility, immediate focus-loss release, capture debounce, peer leases and independent fullscreen protection.
- Validation: 248 tests passed, 1 pre-existing skip; point-lock, outside-client, negative-monitor, focus-loss and input classification cases included. Both PCs deployed .23; receiver and controller logs confirmed captured/relative mode. The user verified normal Zenless Zone Zero camera movement and normal Alt+Tab return on 2026-10-30. This does not establish compatibility with all games.

# Standalone 0.102.2933.22 — bidirectional fullscreen edge protection

- Advertise foreground fullscreen crossing protection to authenticated peers separately from hidden-cursor/relative capture. The controller now blocks edge crossing while operating a protected fullscreen peer with absolute coordinates, including games that keep their cursor visible.
- Fullscreen protection works independently of automatic relative game mode and does not force relative input or hide a visible game cursor. Alt+Tab / Win+D to a windowed application or desktop releases the foreground guard.
- Keep fullscreen and capture state ordering independent. Reject unauthenticated/malformed frames and expire disconnected/stale peer protection; existing forced-switch shortcuts remain available.
- Show fullscreen protection status on the game-mouse page. Enable the existing fullscreen crossing protection setting on both computers and update both to .22. Older peers ignore the additional state frame.
- This release addresses fullscreen edge escape. The separate Zenless Zone Zero camera/cursor issue still requires receiver-side diagnostics and is not claimed fixed.

# Standalone 0.102.2933.21 — background game crossing policy

- Exclude desktop/taskbar/task-switch surfaces (including WorkerW), minimized, invisible and DWM-cloaked windows from automatic game capture. Recheck foreground identity after sampling to avoid attributing a stale clip to a new foreground window.
- Do not suppress the desktop's fallback cursor just because a background application left global hidden-cursor state behind.
- Check the actual foreground client rectangle against its monitor for the optional fullscreen crossing guard. The desktop-wide notification state no longer makes a background fullscreen game block crossing from another window.
- Clarify that the existing relative-movement checkbox applies on the desktop too; leave it off when only automatic game-relative movement is desired. Saved user preferences are not globally migrated by this release.
- Regression coverage includes WorkerW plus stale fullscreen clip, Win+D focus loss, minimized/cloaked games, maximized/windowed foreground applications, and negative monitor coordinates. Actual two-PC background-game crossing requires user verification.

# Standalone 0.102.2933.20 — game cursor focus and input timing

- Make the shared cursor a nonactivating overlay, including show, click/hover activation, and transparent/tool-window styles. Its previous normal-window activation behavior could make a game lose focus and release capture.
- In automatic mode, respect intentional application cursor hiding even without a detected clip rectangle. Preserve the desktop fallback for Windows CURSOR_SUPPRESSED; never manipulate another application's ShowCursor counter.
- Probe foreground cursor capture immediately before received mouse input, closing the UI timer's first-event gap. Use native input while respecting a hidden cursor, including the period before game-mode negotiation completes.
- Record authenticated remote capture/desktop transitions and display the latest transition in the settings page, so inspecting the UI after Alt+Tab does not lose all evidence of the preceding mode.
- Validation: new overlay activation regression failed on .19 and passed after the fix. Full suite: 240 total, 239 passed, 0 failed, 1 pre-existing skip. Added intentional hiding versus OS suppression/API-failure cases. Actual two-PC Zenless Zone Zero camera/cursor behavior remains unverified; this release fixes identified code defects and does not claim complete game compatibility.
- Update both computers to .20 for this fix. Input injection and fullscreen-detection limitations remain unchanged.

# Standalone 0.102.2933.19 — automatic game mouse mode

- Detect foreground cursor hiding and clipping, debounce capture entry, and release immediately when the cursor is visible or focus changes. Single-monitor hidden fullscreen is a heuristic; a settings toggle and live status allow opting out of false positives.
- Extend the existing authenticated capability frame with bounded, ordered, expiring game state and normalized cursor position. Older peers ignore the optional payload; both PCs need .19 for automatic coordination.
- Temporarily send relative motion, suppress synthetic cursor overlays, and prevent edge switching during capture. Preserve saved desktop preferences and explicit machine-switch hotkeys. Captured movement uses native SendInput instead of WinRT pointer injection.
- Rebase desktop coordinates to the remote cursor on exit; restore safely on disconnect or a missing heartbeat. Drop queued absolute moves during capture negotiation instead of warping the game camera.
- Add a default-enabled game mouse setting with bidirectional preference sync; old settings snapshots leave the new preference unchanged. Runtime game state is never persisted as a preference.
- Validation: 238 tests total, 237 passed, 0 failed, 1 pre-existing skip. Added detection, focus/menu debounce, expiry/disconnect, reordered/wrapped sequence, payload validation, multi-monitor coordinates, preference isolation and old-peer compatibility cases. Real two-PC game behavior remains to be verified; this does not provide hardware Raw Input emulation or anti-cheat bypass.

﻿# Standalone 0.102.2933.18 — smooth audio gaps in both latency modes

- Preserve the last output sample per channel and apply a stateful 5 ms transition at empty/partial underruns, resume and receive queue trims. Progress survives small output reads; uninterrupted PCM remains unchanged after startup.
- Detect sender queue sequence gaps and smooth the next frame instead of hard-splicing dropped audio. Never replay old audio to conceal a gap.
- Add bounded startup/recovery priming to stable mode (initial 80 ms, adaptive up to the configured capacity or 200 ms). This can increase latency in exchange for jitter tolerance.
- Use event-driven low-latency loopback capture with 60 ms capacity, and give microphone capture 60 ms capacity. Stable loopback keeps its existing polling behavior.
- Expose receive underrun/trim diagnostics in both modes and sender queue discard counts. These are local buffer counters, not network packet-loss measurements.
- Align build and provenance version metadata with .18.
- Validation: the first eight new discontinuity cases failed before the fix. Full suite: 229 total, 228 passed, 0 failed, 1 existing skipped. Local silent WASAPI playback and live capture callback timing passed in both modes; no audio was saved or transmitted by the smoke test. Remote hardware and audible results still require two-computer listening.
- Update both computers to cover both sending and receiving changes; save the selected mode before comparing.

# Standalone 0.102.2933.17 — jitter-tolerant low-latency audio and nonblocking input

- Prime low-latency audio before consuming it, adapt from 40 ms up to 100 ms for actual render block sizes and underruns, and request a 40 ms shared WASAPI render buffer.
- Keep a bounded 160 ms receive queue and retain the adaptive cushion on catch-up; fade only rebuffer/trim boundaries. Report target, underruns and discarded duration as local diagnostics, not network packet-loss measurements.
- Allow twelve 10 ms capture frames and 160 ms age before dropping stale samples. Bound audio TCP buffers while preserving authenticated TLS wire compatibility.
- Preserve the existing stable-mode behavior and saved mode selection.
- Move established-connection writes to a bounded ordered per-socket worker; merge only adjacent absolute mouse moves for the same sender/target. Keep keys, buttons, relative movement and clipboard frames in order.
- Bound input socket send buffers, serialize handshake writes with queued stream writes, remove callback button-up sleep and explicit clipboard GC, and recover through the existing connection lifecycle on write failure.
- Accept an audio peer address only if it matches any currently authenticated input connection of that same peer; retain pairing-key and receiver-route validation.
- Suppress redundant acknowledged settings snapshots for 30 seconds, while new revisions and failed/pending applications retry normally.
- Add underrun, jitter batching, stable-mode, input ordering/coalescing/blockage/failure and dual-address admission regressions.

# Standalone 0.102.2933.16 — bidirectional settings and clipboard reliability

- Synchronize saved common settings and complementary audio routes between authenticated paired peers. Either computer can publish edits; no fixed primary.
- Persist a logical revision counter and deterministic tie-break identity, observe pending local edits before applying remote state, and avoid republishing received snapshots as edits.
- Use whole-snapshot conflict resolution. Independent simultaneous edits are not field-merged.
- Preserve local device IDs and gain, reject taking over a third-peer route, and keep unsupported microphone setup pending until local virtual-device configuration is ready.
- Retain local default-device/driver setup, startup preferences, credentials, pairing secrets and update locations.
- Authenticate bounded frames and receipts with pair-key HMAC, receiver-generated challenges and role/identity binding, over existing encrypted transport and live verified input connections. Discover support before sending to older peers.
- Refresh open settings views; preserve unsaved audio edits and display a conflict notice. Settings requests do not reset drag/drop state.
- Replace one-second clipboard suppression with sequence-based echo filtering, debounce and a fallback sequence watcher.
- Add acknowledged helper RPC, reconnect its named pipe after failures, retry clipboard access, serialize outgoing frames and decode only actual inflated text bytes.
- Test encrypted rapid-copy ordering, helper RPC, authenticated sync framing, replay/tampering, conflict convergence, UI-thread state application/persistence, revoked pairing and audio direction/device preservation.
- Automated validation cannot replace two-computer clipboard and listening checks.

# Standalone 0.102.2933.15 — lower audio latency

- Enable a configurable low-latency audio mode for existing and new settings; preserve explicit opt-out through quick setup.
- Request 20 ms system loopback and playback buffers instead of the previous 100 ms defaults.
- Packetize in 10 ms frames; bound the sender to four frames and discard samples aged over 60 ms after stalls.
- Bound live receive backlog to 60 ms and retain the newest 20 ms on overflow; synchronize trim, append and playback reads.
- Combine header and PCM into one TLS write with the unchanged authenticated wire protocol.
- Keep a larger-buffer mode, existing endpoints, pairing, default-device restore and microphone routes.
- Test PCM continuity, overload recovery, stale-frame expiration, cancellation, concurrency, compatibility and UI settings.
- Requested buffer sizes are not measured end-to-end latency or a guarantee of speaker/headset synchronization.

# Standalone 0.102.2933.14 — faster connection establishment and recovery

- Accept incoming sockets immediately using an endpoint display label. Use the existing
  authenticated handshake for identity instead of blocking on reverse DNS before handshake.
- Persist only successfully authenticated peer addresses, normalized and bound to the current
  pairing-key fingerprint, with a 30-day expiry and a four-entry limit. No raw keys in this cache.
- Prefer a verified outgoing address over later inbound addresses, which can be reachable
  in only one direction (observed with inbound IPv6 replacing a working LAN IPv4 address).
- Try the cached address before DNS; explicit mappings and reverse-lookup validation retain
  their semantics. Continue bounded DNS resolution for changed DHCP addresses.
- Drop cache hints that authenticate as another name or fail key validation without blocking
  independent fresh DNS attempts. Never mark a cache hit as an authenticated connection.
- Check missing outgoing connections every two seconds throughout runtime, replacing the old
  one-shot post-startup check. Gate concurrent resolution and per-address workers.
- Bound TCP attempts to three seconds, handshake reads to five seconds, cancel attempts when
  sockets close, and dispose failed clients. Established sockets retain unlimited idle reads.
- Remove arbitrary UI-thread reconnect sleeps; shorten audio's wait for the input connection.
- Add real IPv4/mapped IPv4/IPv6 transport, deadline/cancellation, cache recovery/migration,
  stale-address authentication failure, and repeated retry coverage.

# Standalone 0.102.2933.13 — selectable secondary-PC playback

- Add Both Computers / Headset Only to the secondary computer quick setup; old settings retain Both.
- Headset Only routes all Windows default playback roles to independent VB-Audio Hi-Fi Cable.
  Keep remote microphone reception on the original VB-CABLE; reject shared/ambiguous routes.
- Download the original Hi-Fi Cable installer from its official HTTPS URL only when needed,
  verify pinned archive/executable SHA-256, and show its original installation UI.
  No driver is installed during upgrades. Keep installer identity and donation/licensing links visible.
- Persist pre-switch playback roles, pending driver stage and reboot continuation. Switching back,
  restore and Stop All restore playback defaults while preserving later manual choices.
- Roll back partially applied playback and microphone default changes on configuration failure.
- Test separated routes, migration/persistence, device ambiguity, partial default-change failures,
  restore, tampering rejection and rendered UI; physical Hi-Fi Cable/remote sound awaits validation.

# Standalone 0.102.2933.12 — audio connections from dual-stack input peers

- Normalize IPv4-mapped IPv6 peer addresses to IPv4 before creating the audio sender socket.
  Keep native IPv6 and scope IDs unchanged; dispose failed connection attempts.
- Reproduced Windows error 10049 with the previous IPv6-only socket and mapped loopback peer.
- Log audio status transitions and socket error codes without recording audio or pairing keys.
- Route authenticated TLS PCM/heartbeat/reconnect tests through the production connection
  factory for both audio stream types over IPv4, mapped IPv4 and native IPv6.

# Standalone 0.102.2933.11 — first-time startup registration

- Handle the FileNotFoundException produced by COM dynamic dispatch when a user has no
  pre-existing task, as well as the original COM HRESULT. Preserve permission and other failures.

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
