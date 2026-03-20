# Settings

Configure TurboSuite behavior and family name mappings per-document. All settings are stored in ExtensibleStorage on the active Revit document.

## Settings Groups

### General

| Setting | Default | Used By |
|---------|---------|---------|
| Show circuit comments dialog | On | TurboWire — prompts for circuit comments after wiring |
| Auto-split linear fixtures | On | TurboDriver — splits linear fixtures across multiple power supplies |

### CAD Room Source (2D Workflow)

Configures how TurboName reads room names from linked DWG files. Two modes:

**Block Attributes (default):**

| Field | Purpose | Example |
|-------|---------|---------|
| Block Name | INSERT entity name to search for | `CDA_ROOM` |
| Room Name Tags | Comma-separated attribute tags, concatenated for room name | `003, 002` |
| Ceiling Height Tag | Single attribute tag for ceiling height | `001` |

**Text on Layer:**

| Field | Purpose | Example |
|-------|---------|---------|
| Room Name Layer | CAD layer with room name text | `ANNO_ROOM` |
| Ceiling Height Layer | CAD layer with ceiling height text | `ANNO_ROOM` |

### Family Names

Newline-separated family name lists that control how other commands identify fixture types. Edit these if your project uses non-default family names.

| List | Default Families | Used By |
|------|-----------------|---------|
| Wall Sconce Families | `AL_Decorative_Wall Sconce (Hosted)`, `Z_Wall Sconce` | TurboWire, TurboBubble — spline wire routing |
| Receptacle Families | `AL_Electrical Fixture_Receptacle (Hosted)`, `Receptacle` | TurboWire — spline wire routing |
| Vertical Families | `Step Light`, `Flood Lights`, `Wall Pack`, `Z_Lighted Mirror`, `Z_Picture Light`, `Z_Swing Lamp` | TurboTag — tag placement direction |
| Switch Families | `Switch`, `AL_Electrical Fixture_Switch` | TurboWire — wire endpoint offset |
| Electrical Vertical Families | `AL_Electrical Fixture_Exhaust (Hosted)`, `AL_Electrical Fixture_Exhaust`, `AL_Electrical Fixture_Fireplace Igniter`, `Exhaust`, `Fireplace Igniter` | TurboBubble — vertical switchleg placement |

## Storage

Settings are persisted per-document using three ExtensibleStorage schemas:

| Schema | Content |
|--------|---------|
| `TurboSuiteFamilyNameSettings` | Family name lists (array fields) |
| `TurboSuiteCadRoomSource` | CAD mode, block name, attribute tags, layer names |
| `TurboSuiteGeneralSettings` | Boolean flags for general options |

Settings are cached in memory and reloaded when the active document changes.

## Dependencies

- No Revit-side families, parameters, or project setup required
- Settings are metadata only — consumed by other commands at runtime
