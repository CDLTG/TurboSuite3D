# TurboTab

Colors each open Revit document tab with a distinct background for quick visual identification. Groups tabs by project — all documents from the same project share a color. Family documents get a colored top bar instead of a full background fill.

## Usage

Click the **TurboTab** button in the Settings ribbon panel to toggle on or off. The enabled state persists across Revit sessions.

TurboTab auto-starts on Revit launch if it was previously enabled.

## How It Works

1. Locates the AvalonDock `DockingManager` in Revit's WPF visual tree via `MainWindowHandle`.
2. Hooks `LayoutUpdated` to detect tab open/close/reorder events.
3. Matches each `TabItem` to an open `Document` using MFC document pointer reflection.
4. Assigns a color from a 10-color palette. Once a document receives a color, it keeps that color for the session even if closed and reopened.
5. Restores original tab styles on toggle-off — cached at first modification.

## Color Palette

| Index | Color |
|-------|-------|
| 0 | Blue |
| 1 | Green |
| 2 | Yellow |
| 3 | Red |
| 4 | Teal |
| 5 | Purple |
| 6 | Deep Orange |
| 7 | Light Green |
| 8 | Light Blue |
| 9 | Pink |

Colors wrap after 10 documents (document 11 gets color index 1).

## Tab Styling

| Document Type | Background | Foreground | Detection |
|---------------|------------|------------|-----------|
| Project | Full palette color | Auto-contrast (black or white) | `Document.IsFamilyDocument == false` |
| Family | Default background, colored top bar (3px) | Default | `Document.IsFamilyDocument == true` |

Selected and hovered tabs use lightened/darkened variants of the assigned color.

## Settings Persistence

Enabled state is saved to `%APPDATA%\TurboSuite\TurboTabSettings.json` as a single boolean. Defaults to enabled if the file is missing.

## Dependencies

- **Xceed.Wpf.AvalonDock.dll** — ships with Revit 2025; used to navigate the document tab visual tree
- No Revit project-level families, parameters, or setup required
