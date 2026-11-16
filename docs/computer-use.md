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
   On Windows and Linux X11 the same card has an unchecked **continuous interaction** option. Only an explicit user
   choice grants temporary Interact in addition to Browse; the model cannot request/upgrade it through
   tool arguments. It allows unclassified ordinary window-scoped input without per-action approval or
   identical whole-window pixels. The card warns about edits, buttons, clipboard changes and incomplete
   sensitive-action detection. Recognized sensitive/actionable targets still require exact one-action
   confirmation; no permanent, cross-app or whole-desktop permission is created.
3. A successful screenshot returns `frame_id`, image dimensions and the full-image input
   area. Click coordinates are image pixels. Hub scales them to the native window bounds;
   macOS captures exclude shadows. Image bytes reach the very next model request, including
   requests inside a continuous tool loop, and remain attached to their tool result on replay.
4. Supply that `frame_id` for one input. Host-verified ordinary actions run without another
   approval card. Sensitive or unclassified actions require a one-action confirmation even
   in Full Access under browsing consent. Explicit Windows/X11 interaction consent permits unclassified
   ordinary input; recognized sensitive actions still require confirmation. Model-supplied risk labels are not trusted. Confirmation is bound to the
   exact arguments, observation and live permission version, not restored from a saved transcript.
   Windows, macOS and Linux X11 support `click`, `double_click`, `move`, `drag`, `type`, `press_key` and `scroll`.
   Drag uses `x/y`, `end_x/end_y`, and `duration_ms` (100–3000). `press_key` accepts a
   `modifiers` array (`control`, `alt`, `shift`, plus macOS-only `command`). Windows and Linux refuse
   `command` instead of substituting Ctrl. Scroll accepts `x/y` and
   `horizontal_scroll`; omitted coordinates use the image center. Wayland supports the same actions
   through its separate portal contract below.
5. Hub refuses expired (two-minute), spent, moved/resized or owner-changed observations.
   Restarting Hub does not restore live observations. Another conversation's input invalidates
   older observations. Application permission does not make another window's frame valid:
   observe each newly selected window before input. macOS classified browsing actions and Windows
   UI Automation-supported text/scroll actions verify the assessed control instead of requiring
   identical whole-window pixels. Windows confirmed text edits and shortcuts keep their one-action
   approval but may also use that semantic verifier. Pointer movement on Windows/macOS only needs
   live permission, window identity/geometry, focus, bounds and occlusion checks. Actions without
   a scoped semantic verifier retain strict frame verification under browsing or sensitive-action
   confirmation. Explicit Windows/X11 interaction permits a generic host verifier for unclassified ordinary
   input instead: frame identity/age/geometry, live grant, native focus, hit ownership and takeover guards
   remain enforced. Results explicitly say semantic target/application outcome were not verified.
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
between bounded motion segments; Windows does the same for dragging. Mouse buttons and shortcut modifiers are released in `finally`. An event already handed
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
actions require separate one-action confirmation, including in TextEdit. Windows may additionally
have explicit temporary Interact consent as described above; old browsing grants never acquire it.
There is no application-specific allowlist or permanent remembered approval of unknown controls.

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
  Unsupported, protected or blank captures are refused. Pointer hit tests require the exact target
  root window and PID, including every bounded drag segment. Input checks live permission,
  process incarnation, foreground window and unchanged geometry; typing/shortcuts also stop on
  pointer movement. Keys/buttons already held before delivery cause refusal. Focus handoff is
  permitted only from this Hub or the authorized executable, waits at most 1500 ms for the exact
  target HWND, and stops on third-app takeover. It never uses AttachThreadInput or synthetic Alt
  to bypass Windows foreground policy. Unicode scalars (including surrogate pairs) are delivered
  through KEYEVENTF_UNICODE; Enter/Tab use real virtual keys, CRLF becomes one Enter. Navigation
  keys carry KEYEVENTF_EXTENDEDKEY. Ctrl/Alt/Shift are supported; Command is explicitly refused.
  Key/button releases run even after interruption, and failed delivery/releases are reported.
  Hub never reads or directly writes/restores clipboard contents; verified Copy intentionally
  replaces the clipboard through the application's own shortcut. UI Automation-supported browsing
  actions reuse the session's bounded capabilities. Under browsing consent, supported text edits
  and shortcuts still need one-action confirmation with semantic verification. Explicit interaction
  consent permits ordinary edits and generic input without repeated confirmation. Recognized secure,
  actionable/modal targets retain confirmation and strict verification; unknown targets do so under
  browsing consent. Unrelated carets/animations remain a limitation of strict visual fallback.
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
- **Linux X11:** `wmctrl`, `maim` or ImageMagick `import`, plus `xdotool`/`libxdo.so.3` for input.
  Executable identity is resolved through `/proc/<pid>/exe`. Window geometry/title come from
  `wmctrl -lpG`. Input uses one libxdo connection per call, not one process per character.
  Preflight rejects unavailable capabilities, invalid keys/Unicode and changed windows before input
  approval. Delivery checks identity, geometry, foreground and pointer movement between scalars and
  drag segments; pointer destinations are checked against X11 child-window ownership. Held keys/buttons
  refuse input. Shortcut/button releases run after interruption. CRLF becomes one Enter, Tab is a key,
  and Unicode scalars use libxdo without accessing the clipboard. AT-SPI is optional: supported targets
  bind the owning window, metadata, geometry, bounded text digest and selection; text selection uses
  the Text interface rather than a drag. Browsing supports verified move/scroll/select/copy. Explicit
  temporary interaction permits generic input if semantics are unavailable; recognized sensitive
  controls and menu/submit shortcuts still require confirmation. Native acceptance remains pending.
- **Linux Wayland:** a scoped XDG RemoteDesktop + ScreenCast **window** session. Hub's `axmolhub-portal-helper` (Linux builds only; GIO and PipeWire remain host-provided) asks the desktop for one transient window stream and sends input through RemoteDesktop `Notify*` methods. There is no full-screen fallback, no `xdotool`/`wtype`/`ydotool`/`uinput` path, and no persistent portal token.

The Wayland contract differs from the native backends: the user selects the window, so Hub **cannot
verify** its title, process or executable identity. By default approval covers one call, even in Full
Access. The initial card now offers an unchecked temporary continuous-interaction option. An explicit
choice reuses permission for the same requested stream in this conversation without per-action cards,
and accepts changed content while still validating frame identity, age, dimensions and mapping revision.
It never creates an executable grant, persistent token or restored restart permission. Portal cannot
classify sensitive controls; the option explicitly discloses edits, clipboard changes and unverified
focus. The assistant must still follow the user's task scope and inspect every returned frame.

Each input consumes one `frame_id` and captures a fresh image. Continuous mode can return a new frame
when no visible change occurred, with an explicit unverified-outcome warning; it never replays input.
If Hub is active, input waits at most 15 seconds for the user to switch away, then verifies again.
Already-inactive Hub needs no handoff delay. Other-app focus and takeover remain unverified. Standard
mode retains strict whole-frame comparison and its three-second screenshot baseline delay.

The sharing strip exposes Pause/Resume, Stop and Revoke. Pause closes the stream, cancels in-flight
work and invalidates permits; resume requires a new selection and observation. Stop, revoke,
conversation removal and coordinator disposal close owned sharing. The desktop sharing indicator,
helper exit or stream failure clears live permission. Permission versions prevent queued inputs from
surviving pause/resume. Released keys/buttons are tracked in the helper and released during cleanup,
including stale-mapping/cancellation paths. An event already sent cannot be recalled.

Wayland builds require `gcc`, `pkg-config`, `libglib2.0-dev`, `libpipewire-0.3-dev` and `libspa-0.2-dev` at build time, and the desktop's Portal backend plus PipeWire at run time. Hub does not bundle those services.

## Wayland UTF-8 text paste and approval-card fix (bounded VS Code acceptance passed)

The saved VS Code comparison established missing Unicode characters, not a font problem. The new
continuous-interaction checkbox explicitly includes text paste and clipboard replacement. Only that
user-selected mode starts the helper with `--clipboard` and requests Clipboard Portal access before
RemoteDesktop Start. The desktop must also grant `clipboard_enabled`; absence never silently upgrades
permission. This is an intentional transport change, not a claim that IME or Avalonia caused the defect.

With permission, `type` stages the whole LF-normalized UTF-8 text as a bounded private-protocol payload,
advertises only plain-text MIME types through Portal Clipboard, then posts Ctrl+V through RemoteDesktop.
The helper serves SelectionTransfer using the portal-provided descriptor and acknowledges completion.
Hub never reads, backs up or restores existing clipboard content. The offered text remains available
while Hub owns that sharing session's clipboard, and is cleared on owner loss or helper shutdown.
A transfer acknowledgement is not proof of document insertion; inspect the returned frame. Failed,
unacknowledged or cancelled delivery is not replayed. No X11/global injection fallback is used.

Without Clipboard permission, non-ASCII `type` is refused as a whole before sending any characters;
ASCII retains the key path and its active-IME limitations. Shortcuts remain key events. This prevents
the previous silent partial-string behavior for Unicode calls. Clipboard permission requires fresh
sharing after restarting the new build; old live consent does not gain it.

The Computer Use approval card now has a scrollable body and fixed action footer. Shorter copy keeps
window scope, focus uncertainty, sensitive-action uncertainty and clipboard replacement visible.
Raw capture parameters are collapsed; one-shot input details remain expanded for review.

Release build and the existing image checks passed. The isolated `tests/portal-text-check.c` uses mocked
D-Bus and a private pipe to check Chinese/emoji/multiline UTF-8 bytes, missing permission, invalid text,
foreign-session isolation, transfer failure and owner loss under ASan/UBSan; it never accesses a real
clipboard. Linux CI runs it beside the geometry check. The rebuilt ShellCheck returned **1752/1754**;
all Portal checks, including the 220px limited-card footer check, passed. Waiting-state and sidebar
menu-count failures remain recorded in `tmp/linux-unicode-card-rebuilt-shell{,-run}.txt` / `.log`.
The machine clock was behind older build outputs, so final builds were explicitly rebuilt rather
than trusting earlier incremental results. The subsequent bounded VS Code acceptance is recorded below.

The refreshed local 0.29.1 linux-x64 AppImage was packaged successfully (no version bump):
`artifacts/releases/linux-x64/axmol-hub-linux-x64.AppImage`, SHA-256
`294F412FC9FEE902553F276812BAAF89316E8ED07242675AE088EE9795732EAA`.
Extracted `AxmolHub.dll` and `axmolhub-portal-helper` hashes match the publish output; the extracted
helper probe returned `CAPABILITIES window 3`. This supersedes the earlier package at the same path.
Evidence: `tmp/linux-unicode-package.log`, `tmp/linux-unicode-extract.log`,
`tmp/linux-unicode-release-rebuild.log`, `tmp/linux-unicode-images-final.log`.
At packaging time no installation or live clipboard input had been performed.

### Restarted-package VS Code acceptance

A new window observation reported `Text transport: Portal Clipboard paste (replaces clipboard)`.
The selected VS Code editor showed the saved previous failure `focus-6274 [] END-6274`.
One `type` call appended two new lines with Chinese, emoji, spaces and LF; the returned frame showed
all requested text. Ctrl+S removed the unsaved indicator. A strict UTF-8 read and exact comparison
of `tmp/linux-cu-vscode.txt` confirmed all 92 bytes matched:

```text
focus-6274 [] END-6274
unicode-9157 [中文验证😀] END
second-line-9157 中文 空格 OK
```

This passes one native VS Code Unicode/multiline paste-and-save case, retaining the old failure
as evidence. No direct file write inserted the acceptance text and no failed input was replayed.
The paste and save calls completed under the live session. The user confirmed the reported problems
were resolved, including repeated approvals and clipped approval buttons, and accepted closing this
round. Clipboard bytes were not separately read. Linux pause/resume, revocation during input,
other applications, active-IME configurations, multiple monitors and other desktop backends remain
for later verification during actual use; no further testing is required for this closeout.

The user also confirmed Windows and macOS had already been fully accepted. That is user-reported
acceptance, not a new cross-platform test run by this Linux session. Historical failures and limited
acceptance notes below remain as evidence rather than being silently rewritten as test passes.
The full ShellCheck result remains 1752/1754 as recorded above; closeout does not waive its failures.

## Current Windows delivery status

### Generic interaction authorization update (bounded installed acceptance completed)

The existing application card offers an unchecked interaction checkbox; the live strip identifies
Browsing versus Temporary interaction. The new scope is in memory only, versioned by the same
pause/revoke lifecycle, and available only from an actual native Windows application-session approval.
Existing grants/transcript approvals do not upgrade. User-authorizing a new app replaces the old grant.

Windows input still uses the same native backend regardless of editor. UIA is an enhancement, not a
TextPattern prerequisite under explicit interaction consent. Supported text controls retain semantic
verification and scalar acknowledgement. Missing text semantics may use generic window-scoped input;
the host still attempts metadata-based risk escalation before delivery and again after activation.
Recognized secure/actionable/modal targets and potentially dangerous menu/submit shortcuts keep
separate confirmation and strict verification. UIA failure is not proof of safety: this residual
uncertainty is explicitly part of the user's interaction choice. No Scintilla adapter, executable
allowlist, pixel-similarity tolerance or whole-desktop mode was introduced.

Backend parameter/identity/geometry/foreground preflight occurs before asking for an action card.
Failed delivery records a hashed action signature for the current user turn; repeating the same input
with a new frame cannot automatically create another approval loop. The model may observe again but
must explain the failure and await new user direction before repeating that action. This marker is
in-memory and removed on revocation; it never stores typed text in plaintext. Refusals distinguish
observation/target changes, user takeover, unsupported input, verification failure and possible partial
delivery. No interrupted input is repaired or replayed.

Post-input observation uses a bounded idle/visual-settling wait (up to 150 ms idle plus approximately
600 ms sampling). Stable frames do not prove application success; continued animation/processing is
reported explicitly. Cancellation/revocation stops further settling captures; the normal capture grant
is still checked for the final frame. Native capture/provider calls themselves are not hard real-time.

The existing offline image suite now covers interaction opt-in, browsing non-upgrade, capability ceiling,
cross-app refusal, pause/revoke and fresh-session default. A small isolated metadata fixture checks that
a provider-less Pane requires approval under Browse but can use generic delivery under explicit
Interact, while recognized button/password contexts still require confirmation. This is a necessary
permission-boundary check, not desktop acceptance. Evidence: `tmp/computer-interaction-{images,boundary}.log`.
The shell approval fixture also asserts the new option is initially unchecked. Full ShellCheck returned
1760/1761 with one out-of-scope sidebar menu-count failure (actual menus 8 / rows 8), reproduced twice:
`tmp/interaction-shell{,-final}.txt` and corresponding `-run.log` files. Computer Use assertions passed;
the unrelated failure was not removed or hidden.

The user installed and restarted the package below, explicitly enabled continuous interaction, and
accepted the following real x-studio cases:
- ASCII, Chinese, emoji and multi-line input displayed correctly and the saved UTF-8 file matched
  exactly. No editor-specific adapter was used.
- A continuous double-click selection, Ctrl+C, Ctrl+End, Right, Enter and Ctrl+V sequence appended
  the selected `alpha` correctly while preserving existing content. The user confirmed zero further
  approval cards for all six actions after initial application interaction consent.
- Pause followed by Resume invalidated the old observation: a pointer action was refused with no
  input sent. A fresh observation then allowed pointer movement normally.
- Moving the window invalidated the old observation with an explicit moved/resized refusal, not a
  timeout. A fresh observation allowed pointer movement at the new location.
- Revoke removed the live interaction grant: an old-frame action required fresh application consent;
  the user rejected that request and confirmed the expected behavior. It was not retried.

The user approved closing this change with those bounded results and reported macOS had already
been verified. This is not a new macOS test run. Third-party focus takeover, interruption during a
running action, dedicated two-axis scrolling and mixed-DPI desktop cases were not completed in this
acceptance round; do not describe them as passed. The earlier text-substitution root cause remains
unproven. Notepad's transient Chinese glyph issue disappeared after reopening per user report;
x-studio is the user-selected text-input acceptance reference.

Release build passed with 0 warnings/errors; existing image and tool-policy suites passed. The small
isolated interaction boundary fixture passed without input/clipboard access. Local unsigned 0.29.0
win-x64 packaging succeeded: `artifacts/releases/win-x64/AxmolHub.exe`, SHA-256
`1F3C38986930FDDBEC41B44455FDC91BB90381339CD172F7CA9C74FA69213E81`.
Logs: `tmp/computer-interaction-{build,images,policy,boundary,package}.log`. Optional partner-ID injection
was skipped. No automatic installation or version bump was performed. The unrelated ShellCheck failure
above remains open; this is not an all-green shell or exhaustive desktop acceptance claim.

The Windows input backend now lives in `WindowsComputerUse`, separate from GDI capture. It adds
pointer movement, double-click, segmented dragging, positioned/two-axis scrolling, Ctrl/Alt/Shift
shortcuts, bounded cooperative focus handoff and precise input-start reporting. The screenshot-to-native
coordinate mapping remains shared with the other native backends; no Windows-only scale factor is applied.

### UI Automation browsing and semantic verification update

The Windows host now classifies MovePointer, supported ScrollPattern scrolling, bounded TextPattern
selection and Ctrl+C with a nonempty non-secure selection. No arbitrary click/double-click, typing,
paste, Select All or other shortcut becomes automatically approved under browsing consent. The explicit
interaction scope described above separately authorizes ordinary generic input. Supported text actions can carry
a UIA semantic verifier even when they still require one-shot confirmation. Unknown targets keep
strict visual fallback: there is no pixel-percentage threshold or blanket remembered approval.

The verifier binds runtime identity, PID, authorized window ancestry, control geometry/metadata,
bounded text digest and selection offsets (and scroll state for scrolling). Text is limited to 64K,
read only from non-secure text controls, hashed in memory and not logged. Sensitive labels only
escalate risk; missing words do not establish safety. Buttons, links, menu items and modal/secure
ancestors are not browsing targets. Provider failure/ambiguity returns to explicit confirmation.

The interop uses the native UIAutomationClient ABI on a dedicated MTA, with 500 ms connection and
transaction timeouts. COM objects are released there and never retained across requests. If an inactive
thread has no focused HWND, only a bounded unique visible text-pattern candidate can be assessed;
multiple candidates are not resolved by choosing the first. An existing assessment can resolve its
exact runtime identity across approval focus changes. The assessed runtime identity is resolved independently of global focus on subsequent checks;
a retained focused HWND can be a XAML host and UIA global focus can lag native activation.
After cooperative activation the actual UIA focused element must match before keyboard delivery.
The host waits at most 750 ms for that exact focus to propagate, rechecking the target snapshot,
permission, native focus and geometry without sending input. It never waits through changed text,
selection or provider failures. Continued identity/focus/geometry and pointer checks remain. A different focused control refuses input even after approval.

Classified selection uses UIA RangeFromPoint/Select in the same text control rather than a native
mouse drag, preventing a selection gesture from moving existing text or starting drag-and-drop.
Classified scrolling uses the assessed ScrollPattern rather than global wheel events. Permission,
identity, geometry, pointer takeover and ownership are rechecked during direct delivery. Ordinary
mouse dragging and unsupported scrolling still require explicit confirmation and native safeguards.
UIA evidence is application-provided, not an OS sandbox or proof of arbitrary business semantics.

The isolated read-only UIA fixture under `tmp/windows-uia/` checks browsing classification, stable
verification across caret blink and approval focus, changed text/selection refusal, ambiguous inactive
controls and secure-field fallback. It never posts input or accesses the clipboard. This fixture is
needed to validate the native COM ABI and permission boundaries the compiler cannot check; it is not
installed-app acceptance and adds no permanent ShellCheck suite. Full native acceptance remains pending.

For this update, the Release build passed with 0 warnings/errors, the isolated UIA fixture passed,
and the existing `--check-ai-images` and `--check-ai-tool-policy` checks passed. Evidence:
`tmp/windows-uia/{build,probe,images,policy}.log`. The refreshed unsigned local 0.29.0 win-x64
installer is `artifacts/releases/win-x64/AxmolHub.exe`, SHA-256
`B9086B0DCCFE3848333F8C6629A7259C31889F91EA5D474A0ABDC9357F34804C`.
Packaging succeeded (exit 0), log `tmp/windows-uia/package.log`; optional partner-ID injection was
skipped. No automatic installation, version bump or claim of installed-package acceptance was made.

### UIA focus follow-up (installed acceptance pending)

After installing the first UIA package, the requested multi-line retry in a blank Notepad tab never
reached input: one frame expired at two minutes; a fresh request then returned the generic UIA
verification-unavailable refusal with `No input was sent`. No failed text was replayed. A read-only
probe of that Notepad instance could resolve and verify its inactive text control; the exact active
failure was not reproduced in that probe. This is not evidence that the original failure is solved.

Source review found that verification resolved keyboard targets from global UIA focus immediately
after native activation, although the two focus states need not update together. Verification now
resolves the assessed runtime identity and separately checks actual focused-element equality with
the bounded wait above. Failures report pre-/post-activation stage and HRESULT (or the changed
snapshot field), never provider messages or document content. The existing temporary isolated UIA
fixture now checks delayed/exact focus, wrong focus, cancellation and revocation during that wait,
as well as the previous text/selection/security boundaries. Reports:
`tmp/windows-uia/focus-fix-probe.log` and `tmp/windows-uia-live/fixed-read-only.log`.

Release build passed with 0 warnings/errors and the existing `--check-ai-images` checks passed
(`tmp/windows-uia/focus-fix-{build,images}.log`). The refreshed unsigned local 0.29.0 installer at
`artifacts/releases/win-x64/AxmolHub.exe` has SHA-256
`99ADD9B433AB7E69F4612BD54D9D5132AD5D9B24CEF9B8E2861FB3F78D0AFA42`.
Packaging succeeded; log `tmp/windows-uia/focus-fix-package.log`. This supersedes the earlier packages
at that output path. Installation and real desktop acceptance remain user-driven and pending.

### Text acknowledgement follow-up (installed acceptance pending)

With the focus-follow-up installer, a full three-line input delivered without UIA/pixel refusal, and
user confirmed only one input approval. However, a strict UTF-8 read of the user-saved
`hub-win-retest-9647.txt` found `paste-target:EEND-9647` instead of `paste-target: END-9647`.
The first two lines, Chinese and emoji were correct. User confirmed no manual third-line edits.
This is a real character-substitution failure, not successful exact-text acceptance. It does not
establish that IME state caused the earlier spaces or that the focus fix solved text integrity.

A temporary isolated native edit probe delivered the original text correctly under normal and
artificially delayed message processing; it did not reproduce Notepad's original substitution.
The Windows UIA-supported typing path now acknowledges each Unicode scalar before posting the
next: bounded document text and selection must match the exact expected insertion/replacement,
including spaces, surrogate pairs and line breaks. It checks for intervening text/selection changes
before each scalar, and waits up to 1500 ms for the prior scalar's document/caret update while
retaining permission, native focus/geometry and pointer checks. CRLF and CR are normalized to LF
for semantic comparison only, not rewritten in the user's document. The transient expected text
is call-local, bounded to 64K and never logged. Unsupported controls retain the existing confirmed
native fallback and do not acquire a claim of exact text acknowledgement.

Substitution, provider failure, an unacknowledged character or takeover stops subsequent delivery.
No automatic repair, clipboard insertion, rollback or replay occurs. There may already be a partial
edit, so the result requests observation instead of announcing success from SendInput's queue count.
This is a fail-stop integrity improvement and pacing change, not an established root-cause fix.
A focused temporary fixture is necessary because silent character corruption affects the core input
workflow: the fixture checks exact ASCII/Chinese/emoji/multiline delivery with delayed processing,
and deliberately substitutes/drops a space to verify mismatch/timeout stops without the final marker.
Evidence: `tmp/windows-text-delivery/{baseline,acknowledged}.log`. Its events target only the fixture;
no failed user-document text was replayed. Installed Notepad acceptance remains pending.

Release build passed with 0 warnings/errors; the existing image checks and isolated UIA regression
fixture passed (`tmp/windows-text-delivery/{build,images,uia-regression}.log`). The refreshed unsigned
local 0.29.0 win-x64 installer at `artifacts/releases/win-x64/AxmolHub.exe` has SHA-256
`F6643BC44814B9EF835789B4A2EF29387908F6944ADF5CFD5E30543A1FE45393`.
Packaging succeeded (exit 0), log `tmp/windows-text-delivery/package.log`; optional partner-ID injection
was skipped. This replaces earlier packages at the same path; no automatic installation was performed.

### Earlier local package and bounded desktop findings

The first installed package accepted pointer movement, separate short ASCII and Chinese inputs,
a Unicode emoji and Ctrl+A. The user saved the short text and a strict UTF-8 file read confirmed
`ascii-7296中文😀`; the editor rendered Chinese as boxes despite correct file bytes. Ctrl+C reported
delivery and preserved text, but paste was not completed. Immediate post-input captures sometimes
preceded application processing. A longer first input produced only `hub-win-6184 `, two CRLFs and
37 spaces in the saved file; this failure remains unexplained and is not fixed by UIA classification.
Repeated whole-frame refusals and one-shot approval frequency blocked further acceptance, prompting
the semantic-verification update. No original failed text was replayed.

Release build passed with 0 warnings/errors. Existing `--check-ai-images`, `--check-ai-context`
and `--check-ai-tool-policy` checks passed. Logs: `tmp/windows-computer-{build,images,context,policy}.log`.
The earlier unsigned win-x64 installer was built with `installer/Build.ps1` (exit 0; superseded at the same output path by the UIA package above):
`artifacts/releases/win-x64/AxmolHub.exe`, version 0.29.0, SHA-256
`8132D893629692F3BC7C724E6B74A993D1094E3721195D1E93C2FC3B85664A45`.
Packaging log: `tmp/windows-computer-package.log`. Optional partner-ID injection was skipped.
No version bump or automatic installation was performed.

Installed-package desktop acceptance is pending user installation and joint verification. Build and
offline permission/observation checks do not prove native input delivery. Use a disposable document
and fresh frames to verify click/move/double-click/drag, ASCII/Chinese/emoji, Ctrl+A/C/V, navigation
and scrolling. Also verify pause/revoke, focus/pointer takeover, moved/resized frames, and mixed-DPI
monitors where available. Do not replay an interrupted input. `--drive` control actions are not
substitutes for these native Computer Use tests.

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

## Linux continuity update (local build; installed acceptance pending)

The X11 and Portal implementations now support the shared mouse/key action set. Explicit temporary
continuous interaction avoids repeated approval in the selected scope. Unknown-control behavior and
Portal's unverified identity/focus limitations remain visible in the permission card. Native X11 uses
libxdo and optional AT-SPI; Wayland stays entirely on RemoteDesktop/PipeWire with no global fallback.

Local checks: Release build, image/context/tool-policy suites, the sanitizer-enabled portal geometry
check and an isolated AT-SPI ABI layout/constant check passed. The existing image suite covers portal
permission versions and pause/revoke isolation. The shell approval fixture now verifies explicit
opt-in followed by two portal input calls without new cards and subsequent revocation.

ShellCheck remains failed: the final packaging run returned **1752/1753**, with a sidebar menu-count
failure; Computer Use/Portal assertions passed. Earlier runs also showed intermittent lifecycle,
waiting-state and plan-draft failures; those logs are retained. Logs are under
`tmp/linux-computer-*`. These checks do not prove native input delivery, Unicode integrity, focus
handoff or cancellation during real input. Installation/restart and joint desktop acceptance are
pending. GNOME/KDE/wlroots and multi-monitor coverage must be recorded individually, never inferred
from compilation or an injected bridge.

Local linux-x64 packaging succeeded with the pinned Velopack tool, version 0.29.1 (no version bump):
`artifacts/releases/linux-x64/axmol-hub-linux-x64.AppImage`, SHA-256
`7CBF0920312E9A992EAA1DFD3A7C088A16F5A9A38E8BD2CC1350F7126B90E8FF`.
The extracted helper hash matches the published helper; running its `--probe` returned
`CAPABILITIES window 3`. This verifies package inclusion and capability probing, not native input.
Packaging log: `tmp/linux-computer-package.log`. Optional partner-ID injection was skipped.
The user will restart with this package before joint acceptance; no automatic installation occurred.

### Installed GNOME 50.1 acceptance: partial, Unicode failure open

The restarted local 0.29.1 package captured a separate Debug Hub with a 2560×1760 frame.
The Debug target uses `--drive` with `keep_open: true` and an isolated data root. Earlier drive
scripts had exited normally after their steps; their disappearing windows were incorrectly attributed
to single-instance startup. The existing `IsDrive` broker bypass was already working.

An input attempted while the controlling Hub remained active was refused after the bounded handoff,
with no input sent. After the user switched to the Debug target, a fresh observation and click focused
its empty composer. Two consecutive type calls visibly produced `linux-cu-20615-again`. Ctrl+A
selected that draft and Right collapsed the selection. These are bounded native delivery observations;
approval-card counts still require user confirmation, and Right alone is not proof that no modifier
remained held. The draft was not submitted to any model.

Typing ` 中文验证 😀` reported delivery but only trailing whitespace appeared; a later fresh capture
still showed no Chinese or emoji. Unicode acceptance FAILED. No failed text was replayed or repaired.
Mutter's upstream keysym implementation searches the active keymap and skips unmapped keysyms,
which is a plausible mechanism, not a locally traced root cause. Portal method acknowledgement is
not text insertion acknowledgement. Clipboard transport would require a separately disclosed permission
and ownership contract; it has not been implemented as a fallback. Pause/revoke, dragging, two-axis
scrolling, mixed-DPI and other desktop acceptance remain pending for this package.

### VS Code comparison with English input mode: Unicode failure confirmed in saved file

After the user explicitly focused the editor and confirmed English input mode, Portal typing
inserted `focus-6274` into `tmp/linux-cu-vscode.txt`. A subsequent call requested
` [中文核验😀] END-6274`; the editor displayed `focus-6274 [] END-6274`. Ctrl+S cleared the
unsaved indicator, and reading the saved UTF-8 file returned exactly `focus-6274 [] END-6274`.
This confirms missing Unicode characters in persisted text, not merely a glyph rendering issue,
and reproduces outside Avalonia. It does not establish the compositor/input-method root cause.
Earlier calls with no visible text had uncertain focus and are not valid Unicode comparisons.
No clipboard fallback or replay of failed text was used in this comparison.

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

- Approve a disposable editor once (TextEdit on macOS; the chosen editor on Windows/X11).
  Observe, click/type, double-click, move, drag-select, platform `Ctrl/Command+A/C/V`, and scroll.
  Under Browse only verified browsing actions skip confirmation; under explicit Windows/X11 Interact
  ordinary edits may also continue. Test Portal's explicit continuous mode separately.
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
