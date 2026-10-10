# AGENTS.md

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
  chrome; the default view is just the task at hand. Empty sessions are reused, not stacked.
- **Surfaces must clip.** Zero-width / collapsed containers need `ClipToBounds`; overflowing children
  painting through is a bug, not a style. Put scrollable lists in a bounded (DockPanel fill / Grid
  row) slot — a StackPanel hands out infinite height and breaks scrolling.
- **Reveal on hover, not on first paint.** Per-message actions, session ⋯ menus, secondary buttons
  stay quiet until hovered.
- **Match the established shell**: slim sidebar with the conversation list under the AI assistant nav
  item, centered 780–820px chat column, right-aligned user pill, borderless assistant text, centered
  greeting + suggestion chips on empty state, 28px status strip. The assistant page may also open a
  right-hand **inspector column** (plan review, file diffs) as a real third shell column, not an
  in-page pane — only a real column reaches the window edge the right-edge assertion measures. When it
  is open the chat column is allowed to compress, but never below a **560px floor**; the 780–820 band is
  the at-rest width, not a minimum the inspector may violate.

When in doubt, remove chrome first. New UI must come with `--verify-shell` assertions (object graph
or rendered pixels) — interaction bugs are silent at build time.

## Verify with the project's own data directory

Always start the app against the repository's own `data/` directory:

```powershell
dotnet run --project src/AxmolHub -- --data-root ./data
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

A release is cut by a commit titled `Version x.y.z` (also `Version x.y.z-beta` or
`Version x.y.z (Preview)`). Its **only** change is the version in `Directory.Build.props` — it is the
release marker, not a change, and the release notes skip it. Versions beginning with `0.` and titles
ending in `(Preview)` are published as GitHub Pre-releases.
Anything else belongs in its own commit; if it rides along with the version bump, CI warns and the
commit stays in the notes.

## Project memory

The `.agents` directory holds the project's work logs and local memory. If that folder exists,
access it and read the memory — do not perform any additional inference beyond what it records.
