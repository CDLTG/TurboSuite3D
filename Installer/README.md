# TurboSuite Installer

First-time setup tool for TurboSuite. Users run `TurboSuiteInstaller.exe` from the network share.

## What It Does

- Copies TurboSuite add-in files to the Revit 2025 addins folder
- Writes a `config.json` to `%LOCALAPPDATA%\TurboSuite\` with the server path (auto-detected from the installer's own directory)
- Writes the initial `version.txt` for auto-update tracking
- Can also **uninstall** TurboSuite by clicking the Uninstall button

## Usage

No installation of the installer itself is needed. Users navigate to the network share and double-click `TurboSuiteInstaller.exe`.
