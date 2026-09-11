# Settings

The `TurboSuite.App` folder holds the add-in entry point (`TurboSuiteApplication.cs` — `IExternalApplication`, ribbon-panel registration, `ExperimentalCommandsEnabled` gating) and the **Settings** command (`SettingsCommand.cs` + `ViewModels/SettingsViewModel.cs` + `Views/SettingsWindow.xaml.cs`). Settings are stored in ExtensibleStorage on the active document, cached in memory, and reloaded when the active document changes.

## Settings groups

### General

| Setting | Default | Used By |
|---------|---------|---------|
| Show circuit comments dialog | On | TurboWire — prompts for circuit comments after wiring |
| Auto-split linear fixtures | On | TurboDriver — splits linear fixtures across multiple power supplies |

### CAD Room Source (2D workflow)

Moved out of this dialog into the **TurboName window** (configured where it's used) — see [Shim/Name/README.md](../Name/README.md). Now persists under its own JSON-backed schema (`TurboSuiteCadRoomSourceV6`), written by TurboName, not here.

### Family Names — removed

The editable family-name lists that used to live here are gone. Family classification/finding is now
keyed off a stable **`TurboSuite Role`** type parameter authored into each special model family and each
tag/detail family (see `Core/Shared/Constants/Roles.cs` and `ParameterHelper.GetRole`/`FindByRole`), so a
family can be renamed freely without touching settings or code. Nothing to configure per-project — the
classification travels with the families. The old `TurboSuiteFamilyNameSettings` ExtensibleStorage schema
is abandoned (stale `DataStorage` goes unread and harmless).

## Storage schemas

| Schema | Content |
|--------|---------|
| `TurboSuiteGeneralSettings` | Boolean flags for general options |
| `TurboSuiteCadRoomSourceV6` | CAD room-source config (JSON-backed; written by TurboName, not this dialog) |
