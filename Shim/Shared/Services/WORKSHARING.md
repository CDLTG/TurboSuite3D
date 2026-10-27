# Worksharing

How TurboSuite stays safe on a workshared (central / cloud) model. It covers the facts the design rests on, the shared primitives, the rule for deciding what an operation must borrow, and each module's working set. **Read this before adding a write site to any module.** The short form lives in CLAUDE.md ("Worksharing — lock up front, honor the commit").

## Why this exists

Under worksharing, Revit does **not** refuse a write to an element a teammate owns:

- `Parameter.Set` returns `true`. Nothing throws.
- The denial surfaces only at `Transaction.Commit()`, which returns **`RolledBack`** (Revit failure `3b7fcaec-c01e-4c2e-819f-67ddd102ce1f`, "Can't edit the element until *X* resaves the element to central and relinquishes it…"). An `ExternalEvent` handler's `try/catch` never fires.

So code that ignores the commit status reports a save that Revit discarded. The original bug: TurboSchedule cleared its dirty markers and said "saved" while every edit was gone. Before this work, no TurboSuite command had any worksharing awareness.

## The model: lock up front, never collide

We accept the cost of **holding** elements (a teammate is blocked until we sync) so that we never **collide** (partial writes, phantom saves, per-element denial handling).

1. **Invisible on non-workshared models.** Every primitive returns immediately when `!doc.IsWorkshared`.
2. **All-or-nothing.** An operation borrows its whole working set before writing and runs only if every element is held. There is no skip-and-report and no partial run.
3. **Freshness is checked separately.** `CheckoutElements` grants a *stale* element, so the lease pre-checks staleness itself (see below).
4. **The commit status is still honored**, as a backstop for Revit's implicit borrows (elements we didn't know we'd touch). It is never the primary mechanism.
5. **Sync with Central is the only release.** TurboSuite **never calls `RelinquishOwnership`**. It is document-wide (there is no per-element overload), and it releases elements that carry the user's own finalized, unsynced edits, native ones included. That hands a teammate exactly the collision this design prevents, and no API exposes a per-element "locally modified" flag that could guard it. A lease therefore holds its set until the user's next sync, even after the window closes.

## What Revit actually does

All of this was probed with TurboSpike on live cloud models (Revit 2025/2026), with a teammate holding elements.

| Observation | Result |
|---|---|
| `Set` on a teammate-owned element | returns `true`; commit → `RolledBack` |
| `CheckoutElements` on teammate-owned ids | returns a **short set**, doesn't throw, takes nothing for those ids, so it's safe to call speculatively |
| …on ids you already hold | they come back in the result, so *granted = result ⊇ working set* |
| …on a mixed set | **partial**: the free ids are borrowed, the owned ones aren't |
| …on a **stale** element (central is newer, nobody owns it) | **grants it**. A write then rolls back with `fa1ddd24-8cd5-4d8f-a390-fbd72f91be98` ("You now own the element, but your file is out of date…"). No data loss, but the user only finds out at save time |
| `DataStorage` (ExtensibleStorage stores) | behaves exactly like a model element |
| A teammate owning a **workset** | every element in it reads `OwnedByOtherUser` with the owner's name, so no special handling is needed |
| `CheckoutElements` inside an open `Transaction` / `TransactionGroup` | allowed. **A rollback does not undo the borrow** |
| `GetModelUpdatesStatus` / `GetCheckoutStatus` | local cache, **0–1 ms** for ~500 elements |
| `CheckoutElements` latency (cloud) | ~250 ms cold for ~50 elements, ~30 ms when already held |
| Borrow + sync without editing | the teammate does **not** need Reload Latest afterwards; only an actual edit makes their copy stale |
| Cancelling a sync from `DocumentSynchronizingWithCentral` | works cleanly; the post-event reports `Status = Cancelled` |
| `RelinquishOwnership` | doc-wide; released elements with finalized unsynced edits; `GetRelinquishedElements()` under-reports |

## The primitives (`Shim/Shared/Services/`)

| Primitive | Use |
|---|---|
| `WorksharingLease.Acquire(doc, ids, itemNoun)` → `LeaseResult { IsGranted, DenialMessage }` | Borrow one operation's working set. Call with no transaction open, before the first write. |
| `WorksharingLease.AcquireSession(doc, module, ids, itemNoun)` | A modeless window's session lease: the module's lock store plus `ids`. Call before the window opens. |
| `WorksharingLease.AcquireOrThrow(…)` | `Acquire`, throwing `WorksharingException` on refusal. For a lease taken mid-run in a synchronous command. |
| `CommitGuard.TryCommit(tx)` | `Commit() == Committed`. On `false`, mark nothing saved and surface `WorksharingMessages.RolledBack`. |
| `CommitGuard.CommitOrThrow(tx)` | Throws `WorksharingException` on a rollback. For shared helpers inside synchronous commands; keep it outside any `try/catch` that would swallow it. |
| `ModuleLockStorageService` | One tiny `DataStorage` per modeless module (schema `TurboSuiteModuleLock`, GUID `5c0b8d2e-7a41-4f6e-9b3d-2e8f1a6c4d70`, a single `Module` string) used purely as a lock. Created on first use, in its own committed transaction, and only on workshared docs. |
| `DataStorageHelper.FindStorageId(doc, guid)` / each store's `FindStorageId` | A data store's id for a working set. Null when the store doesn't exist yet: its first save creates it, owned by its creator, so it needs no borrow. |
| `DataStorageHelper.FindDataStorage` | Duplicate data stores (two users each created one before syncing): picks the **lowest ElementId** and warns once per session. No merge. |
| `ModelessWindowGuard.Register(doc, window, forceClose, hasUnsavedEdits?, name?)` | Closes a doc's windows on a successful sync or Reload Latest (the lease is gone and the snapshot is stale). A cancelled or failed sync leaves them open. Windows that pass `hasUnsavedEdits` get a prompt before a sync or project close asking whether to cancel it. |
| `WorksharingMessages` (Core, pinned by tests) | All user-facing wording. `BuildDenial` builds the one refusal message. |

### How `Acquire` decides

1. Not workshared, or an empty set → granted.
2. **Local pre-check, nothing borrowed yet.** If any element's `GetModelUpdatesStatus` is `UpdatedInCentral` / `DeletedInCentral` (stale) or its `GetCheckoutStatus` is `OwnedByOtherUser`, refuse. A refusal here borrows nothing.
3. `CheckoutElements(set)`. If the result covers the set → granted. A shortfall (a teammate's borrow the local cache didn't know about yet) → refused, and **whatever was partially borrowed stays held until sync**. That's the residual cost of never relinquishing.
4. A `CentralModelException` anywhere → "Can't reach the central model."

A refusal shows one message that says who to ask and what to do:

- *"Sam is working in TurboNumber — ask them to sync, then try again."* — a teammate holds the lock store.
- *"3 circuits are being edited by Sam — ask them to sync, then try again."* — a teammate holds elements in the set.
- *"The model has newer changes from central — Reload Latest, then try again."* — the set is stale.

### Lock stores vs data stores

- **Lock stores lease every duplicate**, not the lowest-id one. Two users can each create the lock before either syncs. Validated: after both sync there are two stores, the first user to lease takes both, and the second is refused with the first user's name.
- **Data stores** (room order, panel settings, CAD Room Source…) are separate from the lock and **must also be in the working set** when the module writes them.
- A modeless command whose lease is refused must still **return `Result.Succeeded`**. Otherwise Revit discards the lock store the command may have just created.

## The working-set rule

> **Borrow only what the API calls directly edit or delete.**

These need nothing:

- elements that change only through regeneration (panel loads, the circuit side of membership, tags, sheet views);
- the view that a new annotation or detail element is created in;
- the element a new tag or wire attaches to;
- anything the operation creates.

Probed borrow matrix. ✗ = the write rolled back while the teammate owned that element; in every ✗ case the failing element was exactly the owned one.

| Write ↓ / teammate owns → | panel | circuit | fixture | view | type |
|---|---|---|---|---|---|
| fixture instance parameter | ✓ | ✓ | ✗ | ✓ | ✓ |
| type parameter | ✓ | ✓ | ✓ | ✓ | ✗ |
| circuit parameter (Load Name) | ✓ | ✗ | ✓ | ✓ | ✓ |
| `RemoveFromCircuit(fixture)` (touches fixture, circuit, panel, tags) | ✓ | **✓** | ✗ | ✓ | ✓ |
| panel parameter | ✗ | ✓ | ✓ | ✓ | ✓ |
| view parameter (also borrows its **viewport**) | ✓ | ✓ | ✓ | ✗ | ✓ |
| `view.HideElements` | ✓ | ✓ | ✓ | ✗ | ✓ |
| new detail line / `IndependentTag` / wire | ✓ | ✓ | ✓ | ✓ | ✓ |

| Panel-side write ↓ / teammate owns → | panel | schedule view | circuit |
|---|---|---|---|
| rename the panel schedule view (borrows its viewport) | ✓ | ✗ | ✗ |
| `circuit.DisconnectPanel()` (touches every circuit on the panel) | **✓** | ✓ | ✗ |
| `DisconnectPanel` + `SelectPanel(panel)` | **✓** | ✓ | ✗ |

What follows from the matrices:

- **Circuit membership is fixture-side.** It needs the fixtures, not the circuit or the panel.
- **Changing a circuit's panel needs the circuit only.**
- **View-level edits need the view.** Creating content in a view doesn't.

**Not yet probed** (the rule's prediction is in use; see "Open questions"):

- `AddToCircuit` / `ElectricalSystem.Create` are taken as fixture-side.
- Panel-schedule slot ops are taken as needing the schedule view + panel + touched circuits.
- Deleting an element is taken as a direct edit of it.
- A teammate owning a viewport while we edit its view is unknown.

When the rule is unsure, **borrow more, not less**. Over-borrowing only blocks a teammate until sync. Under-borrowing falls through to the commit backstop. Neither loses data.

## Implementation patterns

### Modeless windows (TurboNumber, TurboZones, TurboName, TurboRPS, TurboSchedule)

- **Session lease before the window opens** (`AcquireSession`): the lock store plus every data store the window auto-saves. If it's refused, show the message, don't open the window, and return `Succeeded`.
- **Per-operation lease on hot elements** (circuits, panels, devices, driver types). These are borrowed on the click that writes them, not for the whole session, so teammates aren't blocked for hours by an open window.
- **Every commit goes through `TryCommit`.** On `false`, report it and leave the UI dirty or unsaved. Core contracts carry the result: `ILoadNameWriter.UpdateLoadNames` and `IRpsRevitOperations.SwapDriverTypes` return `int?`, and `SetDeferred` returns `bool?` (null = refused or rolled back, already reported).
- **Register with `ModelessWindowGuard`.** Explicit-save windows also pass `hasUnsavedEdits`. Auto-saving windows never prompt.

### Synchronous commands (TurboDriver, TurboWire, TurboBubble, TurboTag, TurboMask, TurboSetup, TurboNudge, TurboHighlight)

- **Lease before the first write.** The command shows the denial and returns without writing.
- **Anything found mid-run** (wires to delete, a circuit created halfway) is leased with `AcquireOrThrow`, and the shared helpers commit with `CommitOrThrow`. The command catches `WorksharingException`, shows its message, and **returns `Result.Failed`**, so Revit discards every commit of the run. A rollback halfway through never leaves the model half-done. That includes TurboDriver's split, whose deleted original fixture comes back. As a result, a rollback for *any* reason now aborts the run instead of continuing past it.
- **Gate expensive working-set queries on `doc.IsWorkshared`.** Building the set must not cost anything or throw on a non-workshared doc. Two places already learned this. TurboName's linked-import lookup is view-scoped and throws on unfilterable views such as schedules. TurboBubble's re-run tag scan is doc-wide, so `LeaseRerun` takes a deferred query.

### Adding or changing a module

1. List every write site: transactions, `Set`, `Delete`, view edits, store saves.
2. For each one, decide what it **directly** edits or deletes (the rule above). Add a data store when the site writes one.
3. Pick a scope: **session** for a window's lock and stores, **operation** for anything a teammate is likely to want too.
4. Lease before the first write. Honor every commit. On a refusal or rollback, mark nothing saved.
5. Check the non-workshared path: no new dialogs, no extra collector cost, identical results.
6. Update the table below.

## Per-module working sets

| Module | Scope | Working set |
|---|---|---|
| **TurboSchedule** | session | lock + every lighting fixture/device `FamilySymbol` (placed or not) + workbook-path store |
| **TurboNumber** | session | lock + room order, room location, keypad-sort and prefix/suffix stores |
| | operation | panels (panel settings); devices (Switch IDs); slot ops: the schedule view + its panel + the circuit in every touched cell, or the whole panel for Sort by Room (`PanelScheduleService.LeaseSlots`, conservative until slot ops are probed). Helpers in `Shim/Number/Services/NumberWorksharing.cs` |
| **TurboZones** | session | lock + panel-settings + one-line-view stores |
| | operation | Load Names Apply: its circuits + that tab's room-override store. One-line redraw: the content it wipes + stale sheets. A refused page draw also silences the chained wire legend |
| **TurboName** | session | lock + CAD Room Source store + **the view** + its linked CAD imports (nearly every write is view-level; the import pin is flipped to force regen). Built only when workshared |
| | operation | auto-generate: the regions it clears; Assign Room Names: its regions |
| **TurboRPS** | operation | retyped drivers (+ any inactive target type it activates); the circuit carrying the defer entity |
| **Settings dialog** | operation | the General Settings store, leased on save |
| **TurboDriver** | operation | selected fixtures + (when they're on one circuit) the circuit, every element on it and the wires between its existing supplies. Leased before `GetOrCreateCircuit` (`DriverCommand.LeaseWorkingSet`) |
| **TurboWire** | operation | fixtures + their existing circuits (+ pre-selected circuits) up front (`LeaseCircuitWork`); re-run wires leased where found (`AcquireOrThrow`) |
| **TurboBubble** | operation | **the view** (the flip-point prompt sets `view.SketchPlane`, a view edit) + the switchleg tags and stub wires it deletes (`LeaseRerun`) |
| **TurboTag** | operation | every tag in the view on the selected elements (a superset of the re-run deletes); combined mode adds the run members (Run Length write) |
| **TurboMask** | operation | selected TurboMask groups (group + type + members), prior stamps + masking regions at the fixtures, fixture tags in the view, selected detail lines (copy-then-delete) (`RerunWorkingSet`). The view is not edited. The stamp family loads only when missing, so it's always new |
| **TurboNudge** | operation | the one keypad, leased before the corner pick |
| **TurboHighlight** | operation | the active view (V/G subcategory or filter visibility) |
| **TurboSetup** | operation | Project Setup: every original level (renamed or deleted), plan views on them (cascade), `AL_` templates (`ProjectSetupWorkingSet`). Name Spaces: the Spaces it may rename |
| **TurboCompact** | — | family documents are never workshared |
| **TurboDocs, TurboSnoop, TurboTab, TurboFixtureFilter** | — | read-only |
| **TurboDMX, TurboDALI** | — | **not leased yet.** Apply this pattern when they ungate |

## What users see

- **Non-workshared model:** nothing new.
- **Workshared, uncontended:** everything works as before. The user silently holds what they touched until they sync. A successful sync or Reload Latest closes open TurboSuite windows; reopening continues.
- **Blocked:** refused before anything is written, with one message naming the holder or saying Reload Latest. Input stays as typed.
- **Implicit-borrow backstop (rare):** Revit's own owner-named failure dialog, then "Revit rolled back the change — nothing was saved."
- **Teammates in native Revit** get Revit's own "borrowed by…" message on anything a TurboSuite user holds, until that user syncs. If the holder *edited* the element, the teammate also needs Reload Latest after that sync.

Known friction (not data loss):

- After a TurboZones Load Names Apply, that user holds the room-override store until sync. A teammate's TurboWire/TurboDriver run that persists a room override is refused after they fill in the circuit-info dialog.
- TurboName holds its plan view and CAD imports for the whole session.
- TurboSchedule holds every lighting type while the window is open, even if the user only looks.

Optional upgrades if blocking becomes a complaint: acquire on first edit instead of on open, a "holding until sync" label, and "Save and sync" (writing dirty edits inside the pre-sync event; unprobed).

## Open questions

These need a second user on a workshared job. Until they're answered, the predictions above are in use.

- **Panel-schedule slot ops** (`MoveSlotTo`, `AddSpare`/`RemoveSpare`, `AddSpace`/`RemoveSpace`): what they actually need. Answering this narrows `PanelScheduleService.LeaseSlots`.
- **`AddToCircuit` / `ElectricalSystem.Create`**: fixture-side, as predicted?
- **Viewport ownership**: does a teammate owning the viewport block our view edits (TurboName, TurboHighlight, TurboBubble)?
- **Deleting a teammate-owned element**: refused by the lease as expected?
- **Duplicate data stores**: `FindDataStorage`'s warning has never fired on a real data store. Does it interrupt mid-operation?

## Probing it yourself

Use TurboSpike (`Shim/Spike/SpikeCommand.cs`). Probe code isn't kept; rebuild it from these recipes.

- **Denial survey:** the teammate borrows or edits the target without syncing. To test a stale element, they edit **and sync** while you don't reload. For each target id, report `GetCheckoutStatus(doc, id, out owner)`, `GetModelUpdatesStatus`, `GetWorksharingTooltipInfo` and the element's workset. Then run `CheckoutElements` and report which ids came back, re-survey, and call `WorksharingLease` to see the real user-facing message.
- **Implicit-borrow battery:** the teammate holds one element per round with an **edit + Ctrl+Z**, which keeps the borrow without changing anything. Run each write in its own `Transaction` inside a `TransactionGroup` that is then rolled back. Record failures with an `IFailuresPreprocessor` (log each failure and `GetFailingElementIds()`, return `Continue`). Record touched ids with a `DocumentChanged` subscription, and unsubscribe **before** the group rollback. Do no explicit checkout, because the point is to observe Revit's implicit borrows. Between rounds, you sync first, then the teammate.
- **Two-run state:** run 1 commits a marker edit and returns `Succeeded`. Run 2 finds the marked elements **by token** (Revit trims a leading space when the field was empty), probes, then strips the marker.
- **Event probes:** use a static armed toggle: the first run subscribes to the `Application` events and the next run unsubscribes. Show a `TaskDialog` from the handler.
- **Two-machine dev build** (for a second user running TurboSuite): build the 2025 shim with `-p:SkipRevitDeploy=true`. Zip `bin\Debug\net8.0-windows\*.dll`/`*.pdb` (minus `RevitAPI*`) under a `TurboSuite\` prefix. With Revit closed on the other machine, extract it into that machine's `%APPDATA%\Autodesk\Revit\Addins\2025\`. The installed `.addin` already points at `TurboSuite\TurboSuite.dll`.

Hazards, learned the hard way:

- **Never rename a view in a rolled-back battery.** A panel-schedule rename **survived `TransactionGroup.RollBack`**. Renaming a view also renames its view workset, which rides outside the undo. The rename stacked on every run and had to be fixed by hand. Every other probed write did roll back.
- **Never sweep every element's parameters.** A whole-model parameter audit hard-crashed Revit, and the crash can't be caught. Target known ids.
- **RevDev and the ribbon build don't mix once WPF is involved.** After RevDev loads a second `TurboSuite` assembly, the ribbon copy's windows fail with "does not have a resource identified by the URI". `TaskDialog`-only probes are fine under RevDev.
- **RevDev never runs `OnStartup`**, so the `ModelessWindowGuard` sync/reload hooks don't exist there. Validate sync behavior from the installed build.
- **With Revit open, use `/debug`** (a non-deploying build). A normal build can't overwrite the locked `Addins` DLL.
- **Users can't copy `TaskDialog` text**, so expect screenshots and keep probe output compact.
