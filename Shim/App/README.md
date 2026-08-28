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

### Family Names

Newline-separated family-name lists that control how other commands identify fixture types. Edit when a project uses non-default family names.

| List | Default Families | Used By |
|------|-----------------|---------|
| Wall Sconce Families | `AL_Decorative_Wall Sconce (Hosted)`, `Z_Wall Sconce` | TurboWire, TurboBubble — spline wire routing |
| Receptacle Families | `AL_Electrical Fixture_Receptacle (Hosted)`, `Receptacle` | TurboWire — spline wire routing |
| Wall-Mounted Lighting Families | `Step Light`, `Flood Lights`, `Wall Pack`, `Z_Lighted Mirror`, `Z_Picture Light`, `Z_Swing Lamp` | TurboTag — tag placement direction |
| Switch Families | `Switch`, `AL_Electrical Fixture_Switch` | TurboWire — wire endpoint offset |
| Equipment Box Families | `AL_Electrical Fixture_Exhaust (Hosted)`, `AL_Electrical Fixture_Exhaust`, `AL_Electrical Fixture_Fireplace Igniter`, `Exhaust`, `Fireplace Igniter` | TurboBubble — up/down switchleg placement |

## Storage schemas

| Schema | Content |
|--------|---------|
| `TurboSuiteFamilyNameSettings` | Family-name lists (array fields) |
| `TurboSuiteGeneralSettings` | Boolean flags for general options |
| `TurboSuiteCadRoomSourceV6` | CAD room-source config (JSON-backed; written by TurboName, not this dialog) |
