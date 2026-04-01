# TurboSuite Updater

Background process that applies staged updates after Revit closes.

## What It Does

- Waits for all Revit instances to exit (up to 2 minutes)
- Copies staged files from `%LOCALAPPDATA%\TurboSuite\Staging\` to the Revit addins folder
- Updates the local `version.txt` to match the new version
- Cleans up the staging folder

## How It Runs

Users never launch this directly. TurboSuite automatically launches `TurboSuiteUpdater.exe` when the user accepts an update and closes Revit. The update is applied silently and takes effect on the next Revit launch.
