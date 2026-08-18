# Publishing TurboSuite

TurboSuite ships a **separate DLL per Revit version** (net48 for Revit 2024, net8 for Revit 2025, net10 for Revit 2026). Each version is published independently into its own subfolder of the share, and a single **combined installer** at the share root installs whichever version(s) match the Revit installs on a user's machine.

## Share Layout

`publish.ps1` produces this layout under the share root:

```
<ServerPath>\
├─ TurboSuiteInstaller.exe        ← combined installer (version-agnostic, share root)
├─ 2026\                          ← Revit 2026 channel (net10)
│  ├─ TurboSuite.dll, *.dll, *.pdb
│  ├─ TurboSuite.addin
│  ├─ TurboSuiteUpdater.exe / .dll / .runtimeconfig.json
│  ├─ version.txt
│  └─ Archive\<prior-version>\…
├─ 2025\                          ← Revit 2025 channel (net8)
│  ├─ TurboSuite.dll, *.dll, *.pdb
│  ├─ TurboSuite.addin
│  ├─ TurboSuiteUpdater.exe / .dll / .runtimeconfig.json
│  ├─ version.txt
│  └─ Archive\<prior-version>\…
└─ 2024\                          ← Revit 2024 channel (net48)
   ├─ TurboSuite.dll, *.dll, *.pdb
   ├─ TurboSuite.addin
   ├─ TurboSuiteUpdater.exe       (net48 — no .dll/.runtimeconfig)
   ├─ version.txt
   └─ Archive\<prior-version>\…
```

Each version channel carries its own `version.txt` and `Archive\`, so the versions are published, versioned, and rolled back **independently**. The auto-update channel for each Revit version scans only its own subfolder.

## Publishing — run once per Revit version

`publish.ps1` now takes a mandatory **`-RevitVersion`** (`2024`, `2025`, or `2026`). Run it once for each version you want to publish:

```powershell
# Revit 2026 channel
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -ServerPath "\\SERVERNAME\ShareName\path\to\TurboSuite" -RevitVersion 2026 -Version "1.2.0"

# Revit 2025 channel
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -ServerPath "\\SERVERNAME\ShareName\path\to\TurboSuite" -RevitVersion 2025 -Version "1.2.0"

# Revit 2024 channel
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -ServerPath "\\SERVERNAME\ShareName\path\to\TurboSuite" -RevitVersion 2024 -Version "1.2.0"
```

Each run:
1. Builds the solution in Release (both shims + the updater for both target frameworks).
2. Publishes the combined installer to the **share root** (idempotent — refreshed on every run).
3. Archives the channel's currently-deployed version to `<ServerPath>\<RevitVersion>\Archive\<prior>\`.
4. Copies that version's DLLs, `.addin`, and version-matched updater into `<ServerPath>\<RevitVersion>\`.
5. Writes `<ServerPath>\<RevitVersion>\version.txt`.

The two channels can be on **different version numbers** if you only republish one of them — that's expected and supported.

## Publishing Updates

1. Make your code changes (shared logic in `Shim/` or `Core/` reaches every version from a single edit).
2. Add a `## [<version>]` entry to `CHANGELOG.md` (the script warns if it's missing).
3. Run `publish.ps1` with a bumped `-Version`, once per `-RevitVersion` you're shipping.
4. Users are prompted to update on their next Revit launch (per version).

## Rollback — per channel

Each publish snapshots the channel's current share contents to `<ServerPath>\<RevitVersion>\Archive\<prior-version>\` before overwriting. To roll a channel back:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -ServerPath "\\SERVERNAME\ShareName\path\to\TurboSuite" -RevitVersion 2025 -Rollback "1.0.0"
```

This restores all files from `<ServerPath>\2025\Archive\1.0.0\` and updates that channel's `version.txt`. The current deployment is first snapshotted to `Archive\<current>-rolledback-<timestamp>\`, so the rollback is itself reversible. Users see the restored version on their next Revit launch and are prompted to "update" (downgrade) to it.

## Installation (end users)

Users run **`TurboSuiteInstaller.exe`** from the share root. It:
- Auto-discovers the version channels next to it (`\2024\`, `\2025\`, `\2026\`, …) by folder shape — **a future `\2027\` channel needs no installer change**.
- Installs each channel whose matching Revit version is present (e.g. a machine with Revit 2024 **and** 2025 gets both add-ins) into `%APPDATA%\Autodesk\Revit\Addins\{ver}\` and `%LOCALAPPDATA%\TurboSuite\{ver}\`.
- If no matching Revit is found, offers to install all available channels ahead of Revit.

All local state (config, version.txt, staging, updater) is isolated per version under `%LOCALAPPDATA%\TurboSuite\{ver}\`, so multiple Revit versions coexist without interfering.

## Migrating existing flat-layout users (v1.0.0 / v1.1.0) to v1.2.0 (one-time)

v1.0.0 and v1.1.0 stored the add-in and local state in a **flat** layout (`%LOCALAPPDATA%\TurboSuite\` with no version subfolder). v1.2.0 moves everything to **per-version** folders. This is the one update that can't migrate itself, because the auto-updater never repopulates `config.json` at the new path.

**Each existing user must, once:**
1. Close Revit.
2. Run the **new** `TurboSuiteInstaller.exe` from the share and click **Uninstall** — this sweeps the old flat layout *and* any per-version folders clean.
3. Run it again and click **Install** — lays down the fresh per-version layout for their installed Revit version(s).

From v1.2.0 onward, normal auto-update applies and no manual step is needed. (The old flat-layout uninstaller is replaced on the share by the new combined installer, which cleans both layouts — so use the new one for both steps.)

## Notes

- **Supported Revit versions:** 2024 (net48), 2025 (net8), and 2026 (net10). Add a new version by standing up a `Revit{Year}` shim and publishing a `-RevitVersion {Year}` channel.
- Run from a **non-admin** PowerShell (admin sessions cannot see mapped network drives).
- Run from the project root directory.
- Bump the version each release (SemVer: `MAJOR.MINOR.PATCH` — `1.0.0` → `1.0.1` bugfix, `1.1.0` feature, `2.0.0` breaking).
- Breaking changes to ExtensibleStorage schemas, parameter names, or settings shapes still require a coordinated rollout (see `CLAUDE.md`).
- Git tagging and GitHub Release creation are handled separately by the `/release` skill.
