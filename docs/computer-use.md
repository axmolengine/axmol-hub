# Computer Use: application-session permission, observed-window input

Computer Use reuses the normal Agent tool loop. It is not a separate agent, and `--drive`
remains a development harness for Hub itself. Windows, macOS and X11 use verified application
windows; Wayland uses the separate temporary portal-session contract described below. No
fallback to unscoped capture or non-portal global input is permitted.

## Native-window protocol (Windows, macOS, X11)

1. Name the application window in `capture_screen`, or in `computer_use` with
   `action: "screenshot"`. Title ambiguity is refused; `process_id` only narrows the match.
2. Hub resolves the executable, process incarnation and window handle. The first approval
   grants temporary control of that **application** in this conversation, including its other
   windows and newly created windows/processes at the same executable path. Discovering a
   different app never grants permission. Explicitly authorizing another app replaces the
   previous app's control permission. There is no desktop-control mode.
   Existing persisted application grants remain screenshot-only and never upgrade to input.
3. A successful screenshot returns `frame_id`, image dimensions and the full-image input
   area. Click coordinates are image pixels. Hub scales them to the native window bounds;
   macOS captures exclude shadows. Image bytes reach the very next model request, including
   requests inside a continuous tool loop, and remain attached to their tool result on replay.
4. Supply that `frame_id` for one input. Host-verified ordinary actions run without another
   approval card. Sensitive or unclassified actions require a one-action confirmation even
   in Full Access. Model-supplied risk labels are not trusted. Confirmation is bound to the
   exact arguments, observation and live permission version, not restored from a saved transcript.
   macOS supports `click`, `double_click`, `move`, `drag`, `type`, `press_key` and `scroll`.
   Drag uses `x/y`, `end_x/end_y`, and `duration_ms` (100–3000). `press_key` accepts a
   `modifiers` array (`command`, `control`, `alt`, `shift`). Scroll accepts `x/y` and
   `horizontal_scroll`; omitted coordinates use the image center. Extended actions on other
   backends are explicitly refused, not silently converted or dropped.
5. Hub refuses expired (two-minute), spent, moved/resized, owner-changed or content-changed
   observations. Restarting Hub does not restore live observations. Another conversation's
   input invalidates older observations. Application permission does not make another window's
   frame valid: observe each newly selected window before input. Animated or blinking content may require a new frame;
   this is a fail-closed limitation, not a reason to bypass verification.
6. Input delivery is not application success. After delivery Hub captures a fresh image and
   the model must inspect it before deciding what happened. Failed or interrupted input can
   be partial: observe before retrying and do not replay text blindly.

A process-wide desktop lease spans observation, input and the post-input capture. Competing
conversation calls fail instead of interleaving. The live Computer Use strip above the composer provides Pause/Resume, Stop and Revoke.
Pause blocks further delivery and invalidates frames; resume requires a fresh observation.
Stop cancels input and revokes control. Revocation also stops further events in a running action.
Text delivery checks cancellation between characters/code points, and macOS dragging checks
between bounded motion segments. Mouse buttons and shortcut modifiers are released in `finally`. An event already handed
to the OS cannot be recalled. Focus changes or pointer movement during typing stop further
input. Hub may restore focus from its own approval window, but will not steal it from a
third application. These checks are not an OS application sandbox and cannot eliminate all
native-input races or detect every form of human keyboard activity.

## Session lifetime and host risk checks

Control grants live only in memory. Ordinary chat replies do not end the live conversation
permission; Stop, Revoke, conversation removal, workspace/data-root shutdown, failure/cancellation
or an explicit goal completing does. Automatic goal continuation retains permission. Restart
never restores control from conversation JSON or preferences. Closing one window invalidates
that window's observation, not the entire app's permission.

On macOS the host reads Accessibility roles, window ownership, title/bounds and control metadata.
An inaccessible or unclassified control asks for confirmation. Sensitive-label detection only
**escalates** risk; absence of a matching word never establishes safety. There is deliberately no
claim to understand arbitrary GUI business semantics.

The initial ordinary-action adapter is narrow: movement inside the verified window; scrolling
verified scroll areas; copying from non-secure text controls; and document interactions in the
system TextEdit app (not its dialogs or sheets). Other app controls, including Hub controls the
host cannot classify, still ask. An application grant alone does not silently authorize arbitrary
buttons, Enter-to-submit, uploads, deletion, credentials or payments. Inspect the actual card.

`Command+A/C/V` delivery is supported. Automatic paste in TextEdit additionally requires a
clipboard revision observed after this permission's own Copy; an unknown or changed clipboard
requires explicit confirmation. Hub does not read clipboard contents, write a replacement buffer,
or restore a guessed previous value. This revision check is conservative provenance bookkeeping,
not an OS clipboard-isolation guarantee. A delayed Copy may require confirmation on Paste.

Images are stored with existing tool results and sent to the selected model; action/risk/permission
records use the existing transcript and audit path. Audit messages do not add typed text or clipboard
contents. Records are evidence, not an automatic replay script. Screen Recording and Accessibility
are separate OS permissions; granting them does not replay an interrupted action.

## Host requirements

- **Windows:** GDI PrintWindow and SendInput. UIPI can block input to elevated applications.
  Unsupported, protected or blank captures are refused. Occluded click points are refused.
- **macOS:** Screen Recording and Accessibility permissions. Missing permission stops the
  current call; permission approval never queues an automatic replay. The selected window,
  not merely another window belonging to its process, must be frontmost.
- **Linux X11:** `wmctrl`, `maim` or ImageMagick `import`, plus `xdotool` for input. Executable
  identity is resolved through `/proc/<pid>/exe`. Both window geometry and title are parsed
  from `wmctrl -lpG` (the hostname field is not part of the title).
- **Linux Wayland:** a scoped XDG RemoteDesktop + ScreenCast **window** session. Hub's `axmolhub-portal-helper` (Linux builds only; GIO and PipeWire remain host-provided) asks the desktop for one transient window stream and sends input through RemoteDesktop `Notify*` methods. There is no full-screen fallback, no `xdotool`/`wtype`/`ydotool`/`uinput` path, and no persistent portal token.

The Wayland contract differs from the native backends and the UI says so: the user selects the window, so Hub **cannot verify** its title, process or executable identity. An approval covers one call, even in Full Access and for a screenshot; it never creates an application grant. Each input is individually confirmed, uses one `frame_id`, and afterwards a fresh frame is captured. Portal input belongs to the desktop session — keyboard events may reach the currently focused application, pointer events are not guaranteed to stay in the shared window, and Hub cannot detect user takeover. Stop cancels further input but cannot recall an event already sent. Closing the portal session, its desktop indicator, a PipeWire disconnect, a changed frame size, a blank frame, a timeout or a stale frame invalidates the observation; sharing may need to start again.

Wayland builds require `gcc`, `pkg-config`, `libglib2.0-dev`, `libpipewire-0.3-dev` and `libspa-0.2-dev` at build time, and the desktop's Portal backend plus PipeWire at run time. Hub does not bundle those services.

## Current macOS delivery status

The application-session change is built for macOS arm64. Release build and offline image/permission
checks pass. The current full ShellCheck passes (see `tmp/computer-use-shell.txt` for the actual run).
Its previously failing inline-code assertions were repaired at the fixture's space-hit-test anchor;
shared renderer behavior is checked once, with a separate inspector wiring check. No production
selection fix or real-desktop success is inferred from that fixture repair.

The local `.pkg` is for user installation and manual acceptance. Native input, accessibility
classification, clipboard revisions, Retina/multi-monitor coordinates and interruption midway
through a drag still require that installed-app acceptance. No Windows/X11/Wayland desktop
regression is claimed by the macOS build.

## Earlier Wayland verification status (historical)

Implemented and locally checked: Linux helper compilation and output inclusion, per-call portal
approval cards, no permanent application grant, cancellation/refusal cleanup and monitor-source
rejection on an isolated D-Bus fixture. No NuGet dependency was added; the lock file is unchanged.

A real capture on the local GNOME 50 / Ubuntu 26.04 host delivered a readable Hub image, but the
initial helper exposed the full 5120×2880 canvas with black padding (4K monitor, 150% scale).
After adding crop/transform processing, a real capture with the new build returned the complete
2560×1760 Hub window without the large black padding. With the focus-handoff build and English
input mode, a confirmed automatic input changed an empty disposable GTK text field to exactly
`hub-english-5831` (16 characters), visible in the immediately returned screenshot. This verifies
one local ASCII text-input case, not every input mode or scaling configuration. A separate disposable
controls window also verified one button-center click (counter 0 to 1), Tab moving focus to an entry,
and a three-step downward scroll (top visible row 01 to 05). A deliberately confirmed Tab while
remaining in Hub was refused by the three-second Hub-active guard before delivery. These are single
local cases, not full coordinate coverage.

Local boundary checks also exercised a disposable window resizing from 1504×1078 to 1984×1418:
input with the pre-resize frame_id was refused. Closing that window caused pre-input capture to
time out and sharing to stop; no input or automatic reselection occurred. This is bounded safe
refusal, not immediate window-close detection. After a fresh session was stopped through GNOME's
sharing indicator, its helper exited and a confirmed input using the old frame_id was refused,
without opening another picker or starting a helper; the live fixture's key log stayed empty.

The user confirmed that approving and immediately pressing Hub's Stop behaved as expected. The
subsequent fixture check found no recorded keys and no portal helper. The pending capture was
reported as not executed/superseded; this verifies the observed cancelled operation, not interruption
halfway through a character sequence. No further input was attempted or replayed.

These cases complete the agreed local MVP acceptance. Multi-monitor/mixed-DPI and other desktops
are outside that scope and remain unverified, not claimed as tested. The full shell suite failures
below remain separate outstanding issues, not a green full-suite result.

Local verification on the development Wayland host: Release build and linux-x64 self-contained
publish succeeded; image/context checks passed; synthetic frame/coordinate/one-use isolation tests
and four private-bus lifecycle cases passed. The native-window approval fixture now explicitly
supplies its offline backend rather than probing the real desktop, so it runs on Wayland too; real
production bridges retain their platform probe. Workspace assertions now distinguish foreign drive
spellings from host-rooted paths and assert the Windows shell selector independently of the current
OS. The workspace suite passes with no changes to production path guards or shell selection.

That earlier Wayland-host full shell run reached the end: **1758/1765 passed**, still a failed suite. Portal and
native-window approval/input fixtures pass. Six text-selection/copy assertions and one usage-strip
spacing assertion still fail; these are retained, not waived. Windows/macOS native desktop regression
and GNOME/KDE/wlroots acceptance remain pending.

Local `installer/Build.ps1 -Runtime linux-x64` packaging succeeded with the pinned, digest-verified
Velopack tool. The AppImage was extracted; its executable helper matches the published helper by
hash and the PipeWire license is present. The extracted AppRun completed isolated `--smoke` startup
with a nonblank image. The AppImage runtime also completed `--appimage-extract-and-run --smoke`
with HOME/XDG directories and application data/settings isolated under the workspace. This is
package-content/startup verification, not a portal-input test of the AppImage itself. The local package skipped optional partner-ID injection because none was configured.

### Approval focus handoff and input observation

Portal capture waits three seconds after selection/approval before taking the baseline. Each input
also has a fixed three-second handoff after its individual approval. The card tells the user to
Alt+Tab back to the intended app without changing the caret, selection or content. Hub never steals
focus or excludes title-bar/focus-decoration pixels from verification. The entire captured image,
dimensions and mapping revision must still match the baseline; mismatches refuse input.

Hub checks its own activation state before delivery and between text code points, and refuses/stops
if a Hub window is active. This is not verification of the selected app's focus: another app can be
focused while Hub is inactive, so the portal-session warning still applies. Stop cancels the handoff;
an expired or invalidated observation does not become valid merely because the handoff completed.

After input, Hub takes up to three delayed fresh captures (200 ms between attempts) looking for a
visible change. It never replays input. A change is not proof of application success; no visible
change returns an explicit warning and no follow-up input frame_id, requiring a new screenshot.
These are bounded sampling and activation guards, not a guarantee that an application finished
processing its input. The initial delayed-render diagnosis was not established: the user clarified
that the text in the later screenshot was manual input, not evidence of automatic delivery.
With the handoff build, a subsequent attempt produced `和把uto9` instead of `hub-auto-7429`.
Switching to English input mode then delivered the new phrase `hub-english-5831` exactly, as shown
in the immediate post-input screenshot. This comparison supports an active-IME limitation of
simulated keys; it does not establish a general keyboard-injection bug or verify arbitrary Unicode
input. Use English/direct input for literal ASCII typing, maintain focus, and inspect the result.
Hub does not switch input methods automatically. No Clipboard access is requested or changed;
the Clipboard/paste draft was withdrawn because it added permissions and overwrite semantics
outside the current input approval.

### Buffer geometry and fractional scaling

The helper explicitly negotiates `SPA_META_VideoCrop`, `SPA_META_VideoTransform` and header metadata.
It extracts the validated crop, undoes the advertised buffer orientation, and encodes only those
pixels. It honors chunk offsets and row stride; it never finds boundaries by scanning for black.
A missing crop means the full negotiated frame, not a guessed DPI-dependent rectangle. Invalid,
empty or out-of-bounds metadata is refused rather than replaced with a full-frame fallback.

Stationary windows may stop sending buffers until their contents change. For each capture, the
helper pauses the PipeWire receiver, waits for the pause acknowledgement, discards pre-pause pixels,
then resumes and waits for a newly delivered buffer. It does not use the last cached picture as a
fresh observation. Missing pause acknowledgement, resume failure or no resumed buffer still times
out and closes the session. On the local GNOME 50 host this was tested with two consecutive captures
of a stationary disposable GTK window. The later English-mode automatic input test also completed
its pre-input verification and post-input capture without the original static-window timeout.

The native helper retains crop origin, full buffer size and orientation alongside the image.
Click and scroll positions are mapped back through that geometry to the stream's logical `size`,
not scaled as if the cropped window filled the whole stream. An image carries a mapping revision;
any crop/orientation change (including a change and return) invalidates old input even when the
resulting image pixels are identical. Input commands include that revision for a second check in
the helper. Pointer control is refused when the portal omits logical `size`.

This handles GNOME's fixed monitor-sized window-stream canvas without hard-coding Ubuntu versions
or 100%/150%/200% scale factors. It does not by itself prove the cause of another app's capture bug.

## Verification and remaining manual acceptance

Offline checks use isolated fixtures, never a provider endpoint or user data:

```sh
dotnet build src/AxmolHub/AxmolHub.csproj -c Release
dotnet run --project tests/AxmolHub.Checks -- tmp/computer-images --check-ai-images
dotnet run --project tests/AxmolHub.Checks -- tmp/computer-context --check-ai-context
dotnet run --project tests/AxmolHub.Checks -- tmp/computer-workspace --check-ai-workspace
# Linux-only, isolated geometry/input-address contract; also run by Linux CI.
cc -std=c11 -Wall -Wextra -Werror -fsanitize=address,undefined tests/portal-frame-check.c -o tmp/portal-frame-check
./tmp/portal-frame-check
```

Reuse `--verify-shell` for the live application grant, ordinary-input reuse and sensitive/unclassified
confirmation. Its offline fixture checks same-app new-window reuse, conversation isolation,
one-action confirmation and explicit replacement by a different app. `--check-ai-images` also
covers permission pause/revoke/version isolation and drag coordinate bounds. Full-suite failure remains a failure; unrelated assertions must not be suppressed.

Real-desktop acceptance still needs the following on **each** native host:

- Approve the system TextEdit app once. Observe, click/type, double-click, move, drag-select,
  `Command+A/C/V`, and scroll; verified document actions should not show per-input cards.
- Open a second document window. Observe it, then operate without another application grant.
- For an unclassified or mock Send/Delete/Pay control, inspect the one-action confirmation.
  Ordinary-action coverage is limited by the host adapter; do not test real destructive actions.
- Resize/move the window after observation; ensure input is refused.
- Switch to another app or move the pointer while typing; ensure further input pauses.
- Press Stop during typing; ensure no remaining text is sent or automatically replayed.
- Run two conversations against different fixture apps; ensure desktop actions do not interleave.
- Pause/resume and revoke permission; verify stale frames and queued sensitive confirmations fail.
  Without legacy screenshot trust, both screenshots and input must stop after revocation.
- Restart Hub and verify a saved conversation approval does not restore control.
- Verify mixed-DPI/multiple-monitor coordinates, macOS Retina scaling, X11 client geometry,
  and supported hardware-accelerated surfaces. Blank or inaccessible surfaces must refuse.
- Verify another application's windows require explicit application permission; title similarity is irrelevant.

A successful build or an offline bridge fixture does not prove these native desktop cases.

For Wayland specifically, manually accept on GNOME, KDE and a wlroots backend **only if** that
backend advertises RemoteDesktop and WINDOW sharing. Verify selection/cancellation, screenshots,
one confirmed click and short text, Stop, revocation via the sharing indicator, resize/close and
mixed-DPI coordinates. Unlike native-window backends, do not expect focus/takeover or window-position
verification; these are not exposed by the standard portal and the approval warns about that limitation.
