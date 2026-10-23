# TurboFixtureFilter

One keystroke narrows the current selection to **Lighting Fixtures** — the native Modify → Filter dialog's "Check None → tick Lighting Fixtures → OK" without the dialog. Lives in the **Scripts** pulldown (Tools panel, `App/TurboSuiteApplication.cs`) as **Filter to Fixtures**; meant to be driven by a user-assigned keyboard shortcut. One command, no Core/VM split — all logic in `FixtureFilterCommand.cs`.

## Behavior

| Selection | Result |
|-----------|--------|
| contains Lighting Fixtures | selection replaced with just those fixtures (silent) |
| no Lighting Fixtures | message; selection **left unchanged** |
| empty | message |

## Gotchas

- **Selection-only, `TransactionMode.ReadOnly`.** Changing the selection is not a model edit, so there's no transaction and no undo entry — same as the native dialog.
- **Category match is exact.** Only `OST_LightingFixtures`; Lighting Devices, Electrical Fixtures, groups, and tags drop out. A selected Model Group is dropped whole, as the native dialog does — its member fixtures are not pulled out.
- **Linked elements never appear** — `Selection.GetElementIds` holds only host-document elements.
