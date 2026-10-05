# AGENTS.md

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

A release is cut by a commit titled `Version x.y.z`. Its **only** change is the version in
`Directory.Build.props` — it is the release marker, not a change, and the release notes skip it.
Anything else belongs in its own commit; if it rides along with the version bump, CI warns and the
commit stays in the notes.
