# AGENTS.md

## UI interaction charter

The shell is deliberately minimal. GitHub Copilot Desktop is the reference benchmark for visual
language (chat-centric, low chrome, generous whitespace) — copy its *principles*, not its screens.
Axmol Hub is a multi-page developer tool, so adapt rather than replicate; optimize for the local
workflow when it makes sense.

Rules that follow from this:

- **Say a thing once.** The model lives only in the composer chip; the title only in the top bar.
  Don't add a second label repeating information already on screen.
- **Hide secondary things.** History lists, search, and log details live in collapsible/dismissible
  chrome; the default view is just the task at hand. Empty sessions are reused, not stacked.
- **Surfaces must clip.** Zero-width / collapsed containers need `ClipToBounds`; overflowing children
  painting through is a bug, not a style. Put scrollable lists in a bounded (DockPanel fill / Grid
  row) slot — a StackPanel hands out infinite height and breaks scrolling.
- **Reveal on hover, not on first paint.** Per-message actions, session ⋯ menus, secondary buttons
  stay quiet until hovered.
- **Match the established shell**: slim sidebar with the conversation list under the AI assistant nav
  item, centered 780–820px chat column, right-aligned user pill, borderless assistant text, centered
  greeting + suggestion chips on empty state, 28px status strip.

When in doubt, remove chrome first. New UI must come with `--verify-shell` assertions (object graph
or rendered pixels) — interaction bugs are silent at build time.

## Verify with the project's own data directory

Always start the app against the repository's own `data/` directory:

```powershell
dotnet run --project src/AxmolHub.App -- --data-root ./data
```

Without `--data-root`, the app falls back to the per-user data root
(`%LocalAppData%\AxmolHub\data`) and any verification run would operate on the user's real engines,
projects, toolchains, and credentials. `data/` is already in `.gitignore` and must never be
committed.

## Commit style

Follow the repository's existing convention: a short one-line title in the imperative mood, with a
`type:` prefix when it fits — `feat:`, `fix:`, `docs:`, `chore:`. Examples from this repository:

```text
feat: collect commit range into release notes
fix: move conversation deletion into session actions
feat: redesign chat UI around conversations
```

Keep a commit scoped to one change; do not sweep unrelated working-copy modifications into it; do not contains Co-authored-by

- **No long-winded commit bodies.** The commit message body must not be a long essay — keep it short.

A release is cut by a commit titled `Version x.y.z`. Its **only** change is the version in
`Directory.Build.props` — it is the release marker, not a change, and the release notes skip it.
Anything else belongs in its own commit; if it rides along with the version bump, CI warns and the
commit stays in the notes.

## Project memory

The `.workbuddy` directory holds the project's work logs and local memory. If that folder exists,
access it and read the memory — do not perform any additional inference beyond what it records.
