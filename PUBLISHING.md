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

## Notes

- Run from a **non-admin** PowerShell (admin sessions cannot see mapped network drives)
- Run from the project root directory
- Bump the version number each release (uses numeric format: 0.1.0, 0.2.0, ... 1.0.0)
- The script builds the solution, publishes the installer, and copies everything to the share
