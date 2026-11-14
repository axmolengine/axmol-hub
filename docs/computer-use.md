# Computer Use: application-session permission, observed-window input

Computer Use reuses the normal Agent tool loop. It is not a separate agent, and `--drive`
remains a development harness for Hub itself. Windows, macOS and X11 use verified application
windows; Wayland uses the separate temporary portal-session contract described below. No
fallback to unscoped capture or non-portal global input is permitted.

## Native-window protocol (Windows, macOS, X11)

1. Name the application window in `capture_screen`, or in `computer_use` with
   `action: "screenshot"`. Title ambiguity is refused; `process_id` only narrows the match.
2. Hub resolves the executable, process incarnation and window handle. The first approval
   grants temporary **browsing capabilities** for that application in this conversation, including its other
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
5. Hub refuses expired (two-minute), spent, moved/resized or owner-changed observations.
   Restarting Hub does not restore live observations. Another conversation's input invalidates
   older observations. Application permission does not make another window's frame valid:
   observe each newly selected window before input. On macOS, classified ordinary input verifies
   the assessed control instead of requiring identical whole-window pixels. Pointer movement
   only needs live permission, window identity/geometry, focus, bounds and occlusion checks.
   Other native backends and unclassified/sensitive visual confirmations retain strict frame
   verification until an equally scoped host verifier is available.
6. Input delivery is not application success. After delivery Hub captures a fresh image and
   the model must inspect it before deciding what happened. Failed or interrupted input can
   be partial: observe before retrying and do not replay text blindly.

A process-wide desktop lease spans observation, input and the post-input capture. Competing
conversation calls fail instead of interleaving. The live Computer Use strip above the composer provides Pause/Resume, Stop and Revoke.
Pause blocks further delivery and invalidates frames. A tool call started while paused waits for
Resume or cancellation. If its permission version changes during that wait, it refuses before capture
or input and requires a fresh observation and a new action; it does not replay the queued operation.
Stop cancels input and revokes control. Revocation also stops further events in a running action.
Text delivery checks cancellation between characters/code points, and macOS dragging checks
between bounded motion segments. Mouse buttons and shortcut modifiers are released in `finally`. An event already handed
to the OS cannot be recalled. Focus changes or pointer movement during typing stop further
input. Hub may restore focus from its own approval window, but will not steal it from a
third application. These checks are not an OS application sandbox and cannot eliminate all
native-input races or detect every form of human keyboard activity.

## Session lifetime and host risk checks

Observations are resolved from the live registry on each capture/input, not frozen into a model
request's tool declarations: a permission change can replace that registry while the same tool loop
continues. Nullable optional `modifiers` means no modifiers, just like an omitted or empty array.
Refused input reports the actual permission/frame reason (including missing, expired, spent,
wrong-window or invalidated observations) rather than a generic approval failure.

Control grants live only in memory. Ordinary chat replies do not end the live conversation
permission; Stop, Revoke, conversation removal, workspace/data-root shutdown, failure/cancellation
or an explicit goal completing does. Automatic goal continuation retains permission. Restart
never restores control from conversation JSON or preferences. Closing one window invalidates
that window's observation, not the entire app's permission.

On macOS the host reads Accessibility roles, window ownership, title/bounds and control metadata.
An inaccessible or unclassified control asks for confirmation. Sensitive-label detection only
**escalates** risk; absence of a matching word never establishes safety. There is deliberately no
claim to understand arbitrary GUI business semantics.

The live session has an explicit browsing capability ceiling: Observe, MovePointer, Scroll,
SelectText and CopyText. A backend's unqualified Allow does not bypass that ceiling. The first
approval explains that Copy replaces the clipboard; typing, pasting, button clicks and unclassified
actions require separate one-action confirmation, including in TextEdit. There is no Hub-specific
allowlist and no remembered approval of unknown controls.

On macOS, scrolling resolves the hit's enclosing AXScrollArea and verifies that same container;
text copying requires a focused non-secure text control with a nonempty valid selection. Text
selection requires both endpoints in the same text control, bounded text content, AXRangeForPosition,
a settable AXSelectedTextRange and settable AXFocused. It focuses that control and sets only its text
range through AX instead of posting mouse-down/drag events, which could otherwise drag existing
selected text or start drag-and-drop. Unsupported controls still require confirmation for a normal
mouse drag. Generic clicks/double-clicks are never classified as browsing solely by their AX role.
Sensitive/actionable/modal ancestor contexts escalate to confirmation. AX implementations remain
application-provided evidence, not an OS guarantee about arbitrary application business semantics.

For a classified ordinary macOS action the host retains the assessed Accessibility element
for that call only. Before input it rechecks control identity, owning window, geometry and metadata;
text actions also require the same text digest and selection range. A blinking caret or animation
elsewhere is not a veto. Actual text, selection, target or geometry changes still refuse. Text is
read only from non-secure classified controls, bounded to 64K characters, hashed in memory and
never recorded in logs. An unavailable verifier falls back to explicit confirmation plus strict
visual validation; there is no percentage-of-pixels threshold that could hide a changed submit button.
These checks bind the host assessment to delivery, not every pixel to the earlier screenshot.

`Command+A/C/V` delivery is supported. Only verified Copy is covered by browsing consent;
Select All and Paste require separate confirmation. The former TextEdit-specific automatic edits
and clipboard-provenance exception have been removed. Hub does not read clipboard contents,
write a replacement buffer, or restore a guessed previous value.

Images are stored with existing tool results and sent to the selected model; action/risk/permission
records use the existing transcript and audit path. Audit messages do not add typed text or clipboard
contents. Records are evidence, not an automatic replay script. Screen Recording and Accessibility
are separate OS permissions; granting them does not replay an interrupted action.

## Host requirements

- **Windows:** GDI PrintWindow and SendInput. UIPI can block input to elevated applications.
  Unsupported, protected or blank captures are refused. Occluded click points are refused.
- **macOS:** Screen Recording and Accessibility permissions. Missing permission stops the
  current call; permission approval never queues an automatic replay. Before input, Hub can hand focus
  from its own approval UI (or another window of the authorized executable) to the exact selected PID/window.
  On macOS 14+, Hub yields activation to that process instance, requests activation on the main run loop,
  raises the matching Accessibility window, and waits at most 1500 ms for verified focus. It never chooses
  by bundle ID or title alone, and never steals focus from an unrelated foreground application.
  The selected window's PID and Accessibility focused-window title/bounds must match before and throughout
  delivery; a third-app focus change during handoff aborts without target input. Pointer ownership uses
  macOS's system-wide Accessibility hit test and verifies the hit's PID plus its containing
  window's title/bounds. It does not treat overlapping CoreGraphics rectangles as input regions:
  Dock can advertise a monitor-sized layer while receiving no input at the selected point.
  A real Dock/menu/foreign-app hit or an unavailable/ambiguous window remains refused.
- **Linux X11:** `wmctrl`, `maim` or ImageMagick `import`, plus `xdotool` for input. Executable
  identity is resolved through `/proc/<pid>/exe`. Both window geometry and title are parsed
  from `wmctrl -lpG` (the hostname field is not part of the title).
- **Linux Wayland:** a scoped XDG RemoteDesktop + ScreenCast **window** session. Hub's `axmolhub-portal-helper` (Linux builds only; GIO and PipeWire remain host-provided) asks the desktop for one transient window stream and sends input through RemoteDesktop `Notify*` methods. There is no full-screen fallback, no `xdotool`/`wtype`/`ydotool`/`uinput` path, and no persistent portal token.

The Wayland contract differs from the native backends and the UI says so: the user selects the window, so Hub **cannot verify** its title, process or executable identity. An approval covers one call, even in Full Access and for a screenshot; it never creates an application grant. Each input is individually confirmed, uses one `frame_id`, and afterwards a fresh frame is captured. Portal input belongs to the desktop session — keyboard events may reach the currently focused application, pointer events are not guaranteed to stay in the shared window, and Hub cannot detect user takeover. Stop cancels further input but cannot recall an event already sent. Closing the portal session, its desktop indicator, a PipeWire disconnect, a changed frame size, a blank frame, a timeout or a stale frame invalidates the observation; sharing may need to start again.

Wayland builds require `gcc`, `pkg-config`, `libglib2.0-dev`, `libpipewire-0.3-dev` and `libspa-0.2-dev` at build time, and the desktop's Portal backend plus PipeWire at run time. Hub does not bundle those services.

## Current macOS delivery status

### Capability-scoped browsing update (bounded native acceptance completed)

Final Release build passed with 0 warnings/errors; the existing offline image/permission checks passed.
The existing approval fixture was updated to use a classified Move capability instead of treating an
arbitrary Tab key as ordinary. The final full shell run passed 1758/1758 in 120.4 seconds. Evidence:
`tmp/browse-capabilities-{release,images,shell-final-run}.log` and
`tmp/browse-capabilities-shell-final.txt`. No new desktop probe or Hub-only adapter was added.

The unsigned/unnotarized local osx-arm64 package was rebuilt and passed `Test-MacPackage.ps1` (exit 0):
`artifacts/releases/osx-arm64/axmol-hub-0.28.0-osx-arm64.pkg`, SHA-256
`F4C8DA96369026346383B06A54596B2708B51FB90526AE8EC306820CC38BDFE1`.
Logs: `tmp/browse-capabilities-package{,-check}.log`. Optional partner-ID injection was skipped.
After installing and restarting this package, the user confirmed that the current Hub process was
running the new version. In a disposable `browse-acceptance.txt` TextEdit document, two successive
text selections and two Command+C calls returned delivery and fresh frames. The frames showed the
selection moving from the first target line to the second without visible text edits. Clipboard bytes
were not separately read, so this verifies classified Copy delivery, not an independent clipboard-content check.

The user confirmed exactly two approval cards during the sequence and that both were approved:
the initial application-session grant and the final unclassified document click. The four intervening
selection/Copy calls did not ask again. The approved click was then refused by strict visual fallback
with `No input was sent`; this proves the unknown action still required approval, not successful
click delivery or a user-denial path. No input was replayed. The exact visual difference was not
isolated, and unknown-control fallback sensitivity remains a usability limitation.

This completes bounded local acceptance of repeated text selection/Copy under one browsing grant
and separate unknown-action approval. Other apps, unsupported AX text controls, real scrolling,
paste/high-risk actions and multi-monitor behavior were not newly desktop-tested in this sequence;
existing offline/shell boundary checks are not evidence of those native cases.

Semantic-verification refusals now name changed target, metadata, geometry, text or selection; unknown
controls retain strict visual comparison and report that fallback explicitly. Confirmed native dragging
allows up to 100 ms for the OS pointer to catch up only when it is still at the immediately preceding
posted point. It never widens the takeover distance, retries a posted event or tolerates arbitrary movement.
The new verified text-selection path uses no mouse drag at all.

Earlier sections below record historical installed-app acceptance, not acceptance of this update.

### Focus-handoff usability update (local build, desktop acceptance pending)

The installed-Hub / development `--drive` workflow exposed a pre-input frontmost refusal. Source review
found an immediate focus check after asynchronous activation and a global z-order check that treated the
first layer-zero window as the input window. The update uses cooperative, PID-specific activation and
Accessibility focused-window verification with a bounded wait. This is not a relaxation of the in-flight
focus/identity/geometry guards. Permission, observation and host risk checks still precede activation;
unknown controls still need one-action approval. No input action is replayed on activation failure.

The macOS backend now distinguishes refusal before event delivery from possible partial delivery. Both
consume the frame and require a fresh observation. The spin guard permits that requested new screenshot,
without disabling repeated-input failure limits or screenshot-only repetition protection.

Release build (0 warnings/errors), `--check-ai-context` and `--check-ai-images` passed; logs are under
`tmp/computer-use-focus-{build,context,images}.log`. The context fixture reuses the existing spin-guard
checks to cover fresh-observation recovery and the retained screenshot-only bound. No desktop probe was
added. Actual handoff from the installed Hub to a same-title development Hub remains pending installation
and real Computer Use testing; this running conversation still uses the older installed backend.

The local osx-arm64 package was rebuilt with `installer/Build.ps1` and passed
`installer/Test-MacPackage.ps1` (exit 0). Artifact:
`artifacts/releases/osx-arm64/axmol-hub-0.28.0-osx-arm64.pkg`, SHA-256
`B4943A54BA980E3B7F645DDE717702DD6D92F99BE6A0918F7FA081657855A8B0`.
Packaging logs: `tmp/computer-use-focus-package{,-check}.log`. This local installer is unsigned and
not notarized; optional partner-ID injection was skipped because `ORCAROUTER_APP_ID` was unset.
No version bump, release upload or installation was performed.

### Prior installed-app acceptance

The application-session change is built for macOS arm64. Release build and offline image/permission
checks pass. The latest full ShellCheck passed 1758/1758 in 123.0 seconds; see
`tmp/computer-use-followup-shell.txt` and `tmp/computer-use-followup-shell-run.log`.
Its previously failing inline-code assertions were repaired at the fixture's space-hit-test anchor;
shared renderer behavior is checked once, with a separate inspector wiring check. No production
selection fix or real-desktop success is inferred from that fixture repair.

The local `.pkg` is for user installation and manual acceptance. An installed macOS arm64 run
against a disposable TextEdit document returned a 1346x878 image for a 673x439 window. A document
click placed the caret at the intended line end, ASCII typing inserted `hub-input-7429`, and
double-click selected `alpha`. Dragging produced a visible selection and downward scrolling
exposed rows 24-27. These are single-window observations, not complete Retina/multi-monitor coverage.
An initial input while TextEdit was not frontmost was refused; after explicitly bringing the app
forward, a fresh observation was used for subsequent input.

The same run exposed two keyboard failures. `中文验证` appeared as incorrect characters, and an
attempted Command+C replaced the selected `alpha` with `zAsxa` instead of visibly preserving it;
additional unexpected characters were visible in a later frame. That run did not pass copy/paste
acceptance, and the shortcut failure's cause was not established. Keyboard testing stopped without
attempting Paste or replaying the failed text. An event-object-only probe (no input posting) reproduced the
Unicode ABI error: default `char[]` marshalling supplied UTF-8 bytes where CoreGraphics expects UTF-16
code units. The declaration now uses `ushort[]` and native-sized `UniCharCount`; the probe reads back
correct Chinese and surrogate-pair values.

After installing the rebuilt package, the user reported switching from Chinese to English input mode.
In a fresh disposable TextEdit document, the immediately returned image showed exactly
`hub-retest-5831 中文验证 😀`. Double-click selected `alpha`; Command+C preserved the selected text,
and Command+V inserted `alpha` on the separate paste-target line. Command+A visibly selected the
document. A second newly opened TextEdit window in the same process was observed and operated under
the live application permission; its returned image showed `second-window-ok-2648` at the intended
line. These establish one successful Unicode, copy/paste, select-all and same-app new-window case
with the rebuilt code and English input mode. They do not isolate the input method as the cause of
the earlier shortcut failure, nor verify the rebuilt code under Chinese input mode. Clipboard bytes
were not read through a separate tool or replaced for the test; only the selected test word was copied.

In a manual pause/resume case, the user observed that a tool call made while paused stayed pending
until Resume, then returned a missing-host-assessment refusal with no input sent. This matches the
current wait gate, not an immediate paused-state refusal. After the user confirmed Resume, an input
using the pre-pause frame was refused with `No live observation is registered` and no input sent.
A fresh screenshot followed by a window-scoped move reported delivery; the post-input image showed
the test document unchanged. This covers a pending call and fresh-observation recovery, not pausing
midway through native event delivery.

The wait gate now checks the live permission version across the wait and returns an explicit
permission-changed/fresh-observation refusal before either capture or input, rather than falling
through to a generic missing-host-assessment failure. After installing the rebuilt package, a pointer
move requested while paused waited for Resume, then returned the explicit permission-changed refusal
with `No capture or input was sent`. This verifies the updated wait gate for that installed-app input
case; the existing offline image fixture covers both tool entry points without touching the desktop.

After that package installation and Hub restart, the user confirmed that the first screenshot request
showed a new application permission card and that they clicked Allow before the image was returned.
This verifies one restart/re-authorize path: the saved conversation did not silently restore its live
application control permission.

For revocation, an initial post-revoke capture succeeded because the user explicitly approved the
new application permission card; it is not evidence that revocation was bypassed. After revoking
again, the user denied the new card for an attempted pointer move. The tool returned user rejection
rather than input delivery, and the user confirmed this matched expectations. This verifies one
revoke/re-authorize/deny path, not revocation during a running native input sequence. No subsequent
capture or input was attempted after that denial.

Further installed-app acceptance established the following bounded cases:

- After the user moved/resized the TextEdit window, input with a fresh pre-change observation was
  refused with `The window moved or resized after capture` and no input sent. An earlier attempt
  expired after two minutes; that refusal is not counted as geometry validation.
- A long text attempt was interrupted with `user pointer takeover interrupted typing`. Its subsequent
  image ended at row 24 of 30, without that attempt's end marker. No remaining text was replayed.
  This verifies pointer takeover, not Stop: moving toward the Stop button triggered the pointer guard.
- Two other text attempts reached their end markers before the user could click Stop. These are
  inconclusive Stop tests, not failures or successes. Text speed was not altered to manufacture a result.
- With control paused, the user clicked Stop while a pointer-move call was waiting. The call returned
  `application-session permission was revoked` with `No capture or input was sent`; it was not resumed
  or retried. This verifies stopping a waiting call, not stopping native events already in flight.
- With the user reporting Chinese input mode, `IME-RETEST-6184 中文输入验证 😀` appeared correctly.
  Command+C preserved the selected `alpha`, and Command+V inserted `alpha` at the paste-target line.
  The earlier keyboard corruption did not recur in this case; this is not all-IME coverage.
- The user manually changed the clipboard to a known word from the disposable document. A subsequent
  Command+V required one-action confirmation; the user rejected it and the tool reported rejection,
  without input delivery. No clipboard contents were read by a separate tool or restored by the test.
- During a three-second drag-select, the user confirmed switching to Hub with Command+Tab without
  moving the pointer or changing window geometry. The tool refused further delivery, and the next
  image showed an inactive TextEdit window with only `IME-RETEST` selected, short of the planned end.
  This verifies one focus-change interruption of dragging without modifying the document text.
- In a final text attempt (`STOP-TEST-BEGIN-3076`), with the pointer pre-positioned at Stop, the tool
  returned `permission, focus, geometry or pointer ownership changed` and stopped further delivery.
  The user visually confirmed that clicking Stop interrupted the input. This is accepted as one
  end-to-end Stop-click interruption case; the combined refusal does not isolate Stop cancellation
  from focus protection. No remaining text was replayed, and no further capture or authorization was
  requested after the user's confirmation.

The agreed local macOS acceptance is complete with the recorded limitations below. No production
code changed during this final acceptance round, so the installed package remains current.
Independent native in-flight Stop/revocation and broader accessibility classification remain
unverified. A minimal extension of the existing offline image fixture models revocation and
cancellation after the first simulated event: no second event or post-input capture is admitted.
That fixture does not invoke the macOS delivery loop or prove native mouse/modifier cleanup.
Mixed-DPI/multi-monitor and other operating-system desktop acceptance are explicitly excluded from
this local acceptance scope at the user's request; they are not claimed as tested.

The latest local offline run passed `--check-ai-images`, `--check-ai-context` and
`--check-ai-workspace` with `EnableWindowsTargeting=true`; logs are under
`tmp/computer-use-offline-{images,context,workspace}.log`. The image suite was rerun successfully
after adding the minimal wait-version regression case. Debug build and Release osx-arm64 publish/
packaging succeeded, and `Test-MacPackage.ps1` passed for the rebuilt local installer. A subsequent
`--check-ai-tool-policy` run passed (log: `tmp/computer-use-tool-policy.log`). The full ShellCheck
rerun also passed, including the offline application grant, one-action input confirmation, denial,
revocation/re-authorization, cross-conversation isolation and application-replacement fixtures.
No checks were removed or ceilings raised for this run. A native macOS input-method diagnostic was
printed during startup, but the process exited 0 and the report ended with `RESULT: 1758/1758 passed`.

Source review confirms that Stop revokes the live application session before cancelling the run;
the macOS delivery loop rechecks live permission/focus/geometry between code points or bounded drag
segments, and mouse-button/shortcut-modifier cleanup uses `finally`. This is code review, not evidence
that Stop alone interrupted a real OS input sequence. The offline mid-operation revocation/cancellation
extension passed `--check-ai-images` (log: `tmp/computer-use-interruption-images.log`); no real desktop
input was used by it. These automated results do not replace the remaining installed-app cases above.

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
