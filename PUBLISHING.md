# Publishing TurboSuite

## First-Time Deployment

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -ServerPath "\\SERVERNAME\ShareName\path\to\TurboSuite" -Version "0.1.0"
```

## Publishing Updates

1. Make your code changes
2. Run the publish script with a bumped version number:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -ServerPath "\\SERVERNAME\ShareName\path\to\TurboSuite" -Version "0.2.0"
```

3. Users will be prompted to update on their next Revit launch

## Rollback

Each publish snapshots the currently-deployed share contents to `<ServerPath>\Archive\<prior-version>\` before overwriting. To roll the share back to a prior version:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -ServerPath "\\SERVERNAME\ShareName\path\to\TurboSuite" -Rollback "1.0.0"
```

This restores all files from `<ServerPath>\Archive\1.0.0\` and updates `version.txt`. The current deployment is snapshotted to `<ServerPath>\Archive\<current>-rolledback-<timestamp>\` first, so the rollback itself is reversible.

Users will see the restored version on their next Revit launch and be prompted to "update" (downgrade) to it.

## Notes

- Run from a **non-admin** PowerShell (admin sessions cannot see mapped network drives)
- Run from the project root directory
- Bump the version number each release (SemVer: `MAJOR.MINOR.PATCH` — e.g., `1.0.0` → `1.0.1` for a bugfix, `1.1.0` for a new feature, `2.0.0` for a breaking change)
- The script builds the solution, publishes the installer, and copies everything to the share
