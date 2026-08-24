# TurboSuite Updater

Background process that applies staged updates after Revit closes.

## What It Does

- Waits for all Revit instances to exit (up to 2 minutes)
- Copies staged files from `%LOCALAPPDATA%\TurboSuite\Staging\` to the Revit addins folder
- Updates the local `version.txt` to match the new version
- **Retires stale files** listed in the staged `retire.txt`, if present (see below)
- Cleans up the staging folder

## Retire Manifest (`retire.txt`)

Deployment is **additive** — the updater only ever copies files in, never removes ones a new
build stopped producing. So a dependency dropped in a release (e.g. `PdfSharpCore.dll` +
`SixLabors.ImageSharp.dll` when TurboDocs moved to PDFsharp 6.x) would otherwise linger on every
client forever. `retire.txt` closes that gap: after staging the new build in, the updater deletes
each filename the manifest lists from the addins folder — so a file is only removed once its
replacement (or the version that no longer needs it) is already in place.

- **Cumulative / append-only.** The share hosts one current release per channel, and a client can
  jump several versions in one update, only ever seeing the current release's manifest — so every
  filename ever retired must stay listed. The source of truth is repo-root `retire.txt`, which
  `publish.ps1` copies into each channel. Never prune; each line is an idempotent "delete if present".
- **Guarded.** Bare filenames only (path components / `..` rejected); a hard deny-list protects
  live files (`TurboSuite.dll`, `TurboSuite.addin`, `config.json`, and `SixLabors.Fonts.dll` — a
  live ClosedXML dependency, not the retired ImageSharp); deletes are best-effort (a locked file
  logs and retries next update, never failing the update).

## How It Runs

Users never launch this directly. TurboSuite automatically launches `TurboSuiteUpdater.exe` when the user accepts an update and closes Revit. The update is applied silently and takes effect on the next Revit launch.
