# Computer Use: named-window workflow

Computer Use reuses the normal Agent tool loop. It is not a separate agent, and `--drive`
remains a development harness for Hub itself. Wayland is deliberately unsupported: no
fallback to unscoped capture or global input is permitted.

## Protocol

1. Name the application window in `capture_screen`, or in `computer_use` with
   `action: "screenshot"`. Title ambiguity is refused; `process_id` only narrows the match.
2. Hub resolves the executable, process incarnation and window handle. A session or
   permanent application grant permits screenshots, not unattended input.
3. A successful screenshot returns `frame_id`, image dimensions and the full-image input
   area. Click coordinates are image pixels. Hub scales them to the native window bounds;
   macOS captures exclude shadows. Image bytes reach the very next model request, including
   requests inside a continuous tool loop, and remain attached to their tool result on replay.
4. Supply that `frame_id` for one `click`, `type`, `press_key` or `scroll`. Every input has an
   individual approval card, even in Full Access and even for a permanently trusted app.
   This conservative policy also confirms sending, deletion and payment: screenshots cannot
   reliably classify these operations. Review the arguments and the visible target before approving.
5. Hub refuses expired (two-minute), spent, moved/resized, owner-changed or content-changed
   observations. Restarting Hub does not restore live observations. Another conversation's
   input invalidates older observations. Animated or blinking content may require a new frame;
   this is a fail-closed limitation, not a reason to bypass verification.
6. Input delivery is not application success. After delivery Hub captures a fresh image and
   the model must inspect it before deciding what happened. Failed or interrupted input can
   be partial: observe before retrying and do not replay text blindly.

A process-wide desktop lease spans observation, input and the post-input capture. Competing
conversation calls fail instead of interleaving. The existing Stop button cancels input;
text delivery checks cancellation between characters/code points. An event already handed
to the OS cannot be recalled. Focus changes or pointer movement during typing pause further
input. Hub may restore focus from its own approval window, but will not steal it from a
third application. These checks are not an OS application sandbox and cannot eliminate all
native-input races or detect every form of human keyboard activity.

## Host requirements

- **Windows:** GDI PrintWindow and SendInput. UIPI can block input to elevated applications.
  Unsupported, protected or blank captures are refused. Occluded click points are refused.
- **macOS:** Screen Recording and Accessibility permissions. Missing permission stops the
  current call; permission approval never queues an automatic replay. The selected window,
  not merely another window belonging to its process, must be frontmost.
- **Linux X11:** `wmctrl`, `maim` or ImageMagick `import`, plus `xdotool` for input. Executable
  identity is resolved through `/proc/<pid>/exe`. Both window geometry and title are parsed
  from `wmctrl -lpG` (the hostname field is not part of the title).
- **Linux Wayland:** refused. Scoped portal compatibility is a separate future investigation.

## Verification and remaining manual acceptance

Offline checks use isolated fixtures, never a provider endpoint or user data:

```sh
dotnet build src/AxmolHub/AxmolHub.csproj -c Release
dotnet run --project tests/AxmolHub.Checks -- tmp/computer-images --check-ai-images
dotnet run --project tests/AxmolHub.Checks -- tmp/computer-context --check-ai-context
dotnet run --project tests/AxmolHub.Checks -- tmp/computer-workspace --check-ai-workspace
```

Reuse `--verify-shell` for application grants and individual input confirmation. Its fixture
verifies that approval executes once, returns a new image and cannot create permanent input
trust. Full-suite failure remains a failure; unrelated assertions must not be suppressed.

Real-desktop acceptance still needs the following on **each** native host:

- Observe a disposable text editor, approve a click and short text, and inspect the fresh frame.
- Resize/move the window after observation; ensure input is refused.
- Switch to another app or move the pointer while typing; ensure further input pauses.
- Press Stop during typing; ensure no remaining text is sent or automatically replayed.
- Run two conversations against different fixture apps; ensure desktop actions do not interleave.
- Deny/revoke application permission; verify screenshots and input do not bypass the gate.
- Verify mixed-DPI/multiple-monitor coordinates, macOS Retina scaling, X11 client geometry,
  and supported hardware-accelerated surfaces. Blank or inaccessible surfaces must refuse.
- Review a harmless mock Send/Delete/Pay control; ensure the final click has its own confirmation.

A successful build or an offline bridge fixture does not prove these native desktop cases.
