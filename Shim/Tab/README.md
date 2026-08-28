# TurboTab

Colors each open Revit document tab so projects are distinguishable at a glance — all tabs from one project share a color; family docs get a 3px colored top bar instead of a full fill. Toggle from the Settings ribbon panel; enabled state persists (`%APPDATA%\TurboSuite\TurboTabSettings.json`, single boolean, defaults enabled) and auto-starts on launch. Entry `TabCommand.cs`, visual-tree work in `TabColoringService.cs`, persistence in `TabSettingsService.cs`.

## How it works

1. Locate the AvalonDock `DockingManager` in Revit's WPF visual tree via `MainWindowHandle`.
2. Hook `LayoutUpdated` to catch tab open/close/reorder.
3. Match each `TabItem` to an open `Document` via MFC document-pointer reflection.
4. Assign a color from the 10-color palette; a document keeps its color for the session even if closed and reopened. Colors wrap after 10 (doc 11 → index 1).
5. Restore original tab styles on toggle-off — cached at first modification. Never `ClearValue(StyleProperty)` (see CLAUDE.md).

## Palette / styling

Palette in index order: Blue, Green, Yellow, Red, Teal, Purple, Deep Orange, Light Green, Light Blue, Pink.

| Doc type | Background | Foreground | Detection |
|---|---|---|---|
| Project | Full palette color | Auto-contrast (black/white) | `IsFamilyDocument == false` |
| Family | Default + 3px colored top bar | Default | `IsFamilyDocument == true` |

Selected/hovered tabs use lightened/darkened variants. Depends on **Xceed.Wpf.AvalonDock.dll** (ships with Revit) to reach the tab visual tree.
