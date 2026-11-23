# Instructions for Claude sessions

Read NOTES.md first: it holds the decisions.

- Keep Windows-only code out of `NekcaApps.Core`; add or update tests in `tests/` for every Core change.
- Nothing may need admin rights: per-user folders and shortcuts only.
- Never break existing installs: `id`s in apps.json and the folder layout under `%LOCALAPPDATA%\NEKCA Apps` must stay
  compatible; `state.json` changes must read old files.
- Work on a branch and push it; Steve reviews and merges. CI (GitHub Actions, Windows) must be green.
