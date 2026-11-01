# AGENTS.md

## General Rule

- When implementing features, take pride in streamlining and removing legacy redundant code; regard bloated duplicate implementations as bad practice.

## Product charter

The AI assistant is, first, a **general programming and debugging tool** — the same job ChatGPT
Desktop and GitHub Copilot Desktop do: read the code, edit it, run commands, chase a compiler error until
it is fixed. That baseline is the entry requirement, not a feature. Being Axmol-aware (engine source
index, project digest, locked toolchain) comes *second*; it is the reason to use this client rather than a
generic one, but it is not what the assistant is limited to.

- **Never narrow the topic scope.** An Axmol-unrelated question is in scope — answer it. Don't refuse
  because it "isn't about Axmol".
- **Engine knowledge is a layer on top of general capability**, inserted where the model would otherwise
  be wrong (v3 has no reliable corpus online). It is not a fence.

## UI interaction charter

The shell is deliberately minimal. ChatGPT Desktop and GitHub Copilot Desktop are the reference benchmarks
for visual language (chat-centric, low chrome, generous whitespace) — copy their *principles*, not their
screens.
Axmol Hub is a multi-page developer tool, so adapt rather than replicate; optimize for the local
workflow when it makes sense.

Rules that follow from this:

- **Say a thing once.** The model lives only in the composer chip; the title only in the top bar.
  Don't add a second label repeating information already on screen.
- **Hide secondary things.** History lists, search, and log details live in collapsible/dismissible
  chrome; the default view is just the task at hand. Empty sessions are reused, not stacked — and an empty
  session does not take a row either. **A place the user named outlives the sessions in it.** A workspace that
  has run out of conversations keeps its header and says so in one muted line, because the ＋ and the path
  repair live on that header; only removing the workspace makes it leave.
- **Surfaces must clip.** Zero-width / collapsed containers need `ClipToBounds`; overflowing children
  painting through is a bug, not a style. Put scrollable lists in a bounded (DockPanel fill / Grid
  row) slot — a StackPanel hands out infinite height and breaks scrolling.
- **Ask above the box, never over it.** A pending approval, a plan review and a steer draft are questions, and a
  question does not get to cover the input: each one owns a row *above* the composer, newest nearest the box.
  Nothing may paint over `ComposerContent` or grey it out — text half-typed when a question arrives is text the
  person expects to still be there. Focus moves to a card only while the box is empty, and Escape pressed inside
  a text box is never the answer to the card above it.
- **Reveal on hover, not on first paint.** Per-message actions, session ⋯ menus, secondary buttons
  stay quiet until hovered.
- **Draw the chrome yourself.** Whatever the user sees inside Axmol Hub is drawn by Axmol Hub: the main
  window's 32px self-drawn title bar (`Button.titlebar` + `Button.window-control`, dragged through
  `TitleBarDragArea`) is the house style, and a child window wearing the platform's frame beside it reads as
  somebody else's app. For a surface inside the shell, prefer a **layer of the window** to a new `Window`
  whenever it need not leave it — settings moved from a page to an overlay card for that reason — and where a
  real `Window` is right (a modeless build log, a picker on a second monitor), draw its bar too.
  **An internal dialog's bar has one close plate and nothing else** — minimise and maximise belong to the main
  window — so a new dialog gets `HubDialogChrome`, whose spec is the shell's own bar.
  The four verification windows are exempt.
- **A layer leaves the window grabbable.** An overlay covers the shell, not the window: hold it to the row
  under the title bar and stop above the 28px status strip, so the drag lane and the strip's action slots —
  the task queue's restart / download / retry (the only place a pending app update can be acted on) and the
  cancel for a running change — stay pressable while it is up. Geometry checks cannot see a layer that merely
  *looks* clear, so the shared overlay contract checks placement, child-index order, and scrim brush once; each
  overlay adds only its own event wiring or distinct behavior.
- **Match the established shell**: slim sidebar with the conversation list under the AI assistant nav
  item, centered 780–820px chat column, right-aligned user pill, borderless assistant text, centered
  greeting + suggestion chips on empty state, 28px status strip. The assistant page may also open a
  right-hand **inspector column** (plan review, file diffs) as a real third shell column, not an
  in-page pane — only a real column reaches the window edge the right-edge assertion measures. When it
  is open the chat column is allowed to compress, but never below a **560px floor**; the 780–820 band is
  the at-rest width, not a minimum the inspector may violate.

When in doubt, remove chrome first. New UI does not automatically require a new assertion for each
property. Cover new or changed high-impact interactions in the cheapest suitable check; ordinary visual
details remain a manual review unless an explicit acceptance requirement says otherwise.

## Verify with the project's own data directory

Always start the app against the repository's own `data/` directory:

```powershell
dotnet run --project src/AxmolHub -- --data-root ./data
```

Without it the app falls back to the per-user root (`%LocalAppData%\AxmolHub\data`), and a verification run
would operate on the user's real engines, projects, toolchains and credentials. `data/` is gitignored and must
never be committed.

For `--verify-shell`, run the single build-and-check command through Axmol Hub's `run_command` tool so Avalonia
uses the active desktop session. On Apple Silicon macOS, use `/opt/homebrew/bin/dotnet`; elsewhere use the
installed `dotnet` executable. Keep `dotnet run` responsible for both building and checking; do not add
`--no-build`. Give the tool a 900-second timeout because startup, build and UI checks can be
quiet for a while. Do not infer a hang from silence or stop the process early. Keep the report and captured output
under ignored `tmp/`, then inspect them only after the command exits. Keep the command itself shell-neutral so the
runner's exit status is the `dotnet run` exit status; do not append `$?`/`$LASTEXITCODE` capture syntax from a
different shell:

```sh
/opt/homebrew/bin/dotnet run --project src/AxmolHub -- --data-root ./data --verify-shell ./tmp/selfcheck-shell.txt > ./tmp/selfcheck-shell-run.log 2>&1
```

On other hosts, use the installed `dotnet` executable in the same command. Use the tool's timeout setting
(`timeout_seconds: 900`); it is not a shell argument. A successful tool exit and the report's final `RESULT:`
line determine the outcome. Inspect the saved log and report after exit, and preserve and report failures instead
of deleting checks to make the run green.

## Screenshot verification (`--drive`, DEBUG builds only)

To look at a specific state of the real window (after a feature or a UI bug fix), use `--drive <script.json>`. It is
compiled in but inert in release builds, skips the single-instance broker (so it runs beside the user's own Hub), and —
unlike the automation modes — runs against the **user's real data root** (`--data-root` still overrides) without pinning
language/theme; settings are edited on a temp copy so the user's file is untouched. This is the one sanctioned exception
to the `--data-root ./data` rule above; it is for development, packaged installs do not support it.

```sh
/opt/homebrew/bin/dotnet run --project src/AxmolHub -- --drive ./tmp/drive/script.json
```

Script: `{"out":"tmp/drive","steps":[{"do":"navigate","page":"Assistant"},{"do":"click","text":"..."},
{"do":"type","name":"...","value":"..."},{"do":"wait","ms":300|"until":"name-or-text"},{"do":"resize","w":1100,"h":700},
{"do":"language","value":"en-US"},{"do":"screenshot","file":"a.png"}]}`. The exit code and `<out>/drive.evidence.txt`
report each step; the first failing step stops the run. `click`/`type` locate a visible control by `name`
(`Control.Name`) first, else by exact visible `text`. Run it through `run_command` with `timeout_seconds: 300` (the first
build is slow); keep the script and output under ignored `tmp/`. View the PNGs afterwards. See `Services/DriveRunner.cs`.

## Checks and assertions

An assertion is a claim that can be false, in a suite that must still finish. Prefer a small set of checks
that catches distinct, consequential failures over exhaustive enumeration of visible properties. Assertion
counts are measured from each run's `RESULT:` line; they are observations, not targets or quotas.

- **Require a reason to add coverage.** A requested feature or new UI does not imply one assertion per property.
  Prefer updating or reusing an existing check. Add a new claim only when it covers a distinct, consequential
  failure that current coverage misses; record the failure, its impact, and why existing checks cannot catch it.
- **Prioritize by impact.** Automate data integrity, permission and isolation boundaries, core task completion,
  and high-impact silent failures. Leave low-risk appearance details, exact copy, tooltips, and decorative
  dimensions to manual review unless the acceptance criteria explicitly require them.
- **One claim, one result.** Loop equivalent fixture cases and report one result with useful observations. Do not
  combine unrelated claims into a large boolean expression or hide multiple independent checks in one summary;
  reducing output lines alone is not assertion reduction.
- **Name the failure and its impact.** If a build, type, or existing check already catches it, do not add a
  duplicate report assertion. State what user-visible or data-level harm the retained assertion prevents.
- **Print observations, assert invariants.** A value that matters only when something else is red goes in
  `CheckReport.Line` or in that message — a number nobody would act on is the line that goes stale.
- **Justify tolerances.** Distinguish product requirements from measurement tolerances. Keep fixed product
  limits tied to the agreed behavior; document why measurement tolerances fit the observed layout or platform.
  When one changes, explain the cause and retain evidence that it still catches the target failure; there is no
  arbitrary limit on how many times a sound tolerance may be recalibrated.
- **Control external dependencies.** Do not depend on network services, real credentials, the user's data root,
  or a vendor's current live catalog. Use injected transports and fixed protocol samples. Temporary paths,
  synthetic model identifiers, and real dispatcher timing are allowed when the behavior under test needs them.
- **A group fails itself first and never stops the run.** An unbuildable fixture is one `夹具：…` assertion and
  a `return`; what the host cannot supply is `Skip`, never a failure; no `throw` leaves a group.
- **One runtime ceiling.** Keep `SuiteCeiling` as the whole-run timeout and give expensive groups a focused
  switch when that materially speeds iteration. Report measured duration and investigate a suite that approaches
  the ceiling; describe it as a budget overrun unless a specific stalled operation is identified. Quote totals
  and durations only from a dated `RESULT:` line. Do not set assertion-count quotas.
- **The cheapest mode that can see it wins.** Headless for facts that need no live visual tree or dispatcher,
  `--verify-shell` for interaction and layout claims that do, theme/foundation for shared tokens — one claim,
  one primary home.
- **One primary home per failure.** Cover shared component contracts once, and leave each caller to check only
  its own wiring or distinct behavior. Preserve genuinely different protocol, security, compatibility, and
  negative cases; do not keep duplicate evidence for the same failure.
- **Delete the claim with the behaviour.** The same change rewrites or removes assertions invalidated by a
  behavior change. Do not leave checks red, skip them into green, or loosen them without evidence.
- **Track net change.** When adding or removing checks, summarize the net assertion change and the reason. Do not
  impose a mechanical one-in/one-out rule or delete necessary safety coverage to meet a count.
- **Placement and naming.** New groups live in `ShellCheckWindow.<Topic>.cs`, never in the 17k-line main file;
  the name is greppable and stable in the report's language, and the message carries the observed value.

## Line endings are LF

**LF, on every host, in everything Hub writes** — the file `file_write` creates or edits, the JSON state and
conversation files, the logs, the check reports, and the text sent to the model and shown in the shell. The
model emits `\n`, `read_file` shows `\n`, so writing LF is also what makes a copied anchor match first time.
`TextEol.ToLf` is the policy; new write paths go through it rather than inventing their own.

Two exceptions, both deliberate:

- **The wire recording** (`WireLog.TryWrite`) keeps the bytes as they arrived. It exists so a claim can be
  checked against what actually left the machine; folding it would destroy the evidence.
- **Files Hub does not own** (`EngineMirror` editing the engine's `1k/.env`) keep the newline they arrived with.
  Changing one key there must not become a whole-file diff in somebody else's checkout.

Consequences to expect: editing a CRLF file rewrites its whole line endings, so `git diff` there shows every
line (the diff card and the `+N −M` tally normalize both sides and stay honest), and a repository whose
`.gitattributes` pins source to CRLF will fight this. A no-op edit changes nothing — a refused anchor leaves
the file exactly as it was, newline style included.

## Commit style

Follow the repository's existing convention: a short one-line title in the imperative mood, with a
`type:` prefix when it fits — `feat:`, `fix:`, `docs:`, `misc:`. Examples from this repository:

```text
feat: collect commit range into release notes
fix: move conversation deletion into session actions
feat: redesign chat UI around conversations
```

Keep each commit scoped to one change; do not sweep unrelated working-copy modifications into it.
Do not add `Co-authored-by` trailers to commit messages.

- **No long-winded commit bodies.** The commit message body must not be a long essay — keep it short.
- **English only, title and body.** Commit messages are pure English; the entire history is. Chinese
  belongs in the docs and in the display text a test asserts on — never in a commit message.

## Comment language

**Code comments are English, everywhere.** `.cs`, `.csproj`, `.axaml`, `.ps1`, `.props`, `.manifest` —
`//`, `///`, `#`, `<!-- -->` prose is English. This is not cosmetic: terminal windows and CI logs may
open on a host with no CJK font, and a comment that survives there is the one that gets read six
months from now. Write the language into the file from its path before the first comment, not after
a review pass.

Chinese stays where it is *data*, not prose:

- The zh column of the UI-string table (`HubTexts.cs`) — that is shipped product copy.
- Assertion names that a check report prints or a test matches on (the `--verify-shell` check names).
- Expected values in tests when the expected value **is** a Chinese UI string, and non-ASCII test
  fixtures (an install path, a file name) that exist to prove the code handles them.
- A quoted label inside an English comment: write `the "AI 助手" nav item` verbatim rather than
  translating the label — the comment refers to what is displayed.

Anything a terminal renders — an echo line, a sudo prompt helper — is English for the same reason.

A release is cut by a commit titled `ver: x.y.z` (also `ver: x.y.z-beta` or
`ver: x.y.z (Preview)`). Its **only** change is the version in `Directory.Build.props` — it is the
release marker, not a change. Release notes skip `ver:`-prefixed commits only when they change that
file alone; bundled changes stay visible with a warning. An all-version range gets a neutral note,
never a fallback that restores skipped markers. Versions beginning with `0.` and titles ending in
`(Preview)` are published as GitHub Pre-releases.
Anything else belongs in its own commit; if it rides along with the version bump, CI warns and the
commit stays in the notes.

## Project memory

The `.agents` directory holds the project's work logs and local memory. If that folder exists,
access it and read the memory — do not perform any additional inference beyond what it records.
