# TurboCompact

Removes unused materials from the active family document, then compact-saves it. One command, no Core/VM split — all logic in `CompactCommand.cs`. Precondition: `doc.IsFamilyDocument` (else Cancelled with a prompt); runs only in the Family Editor.

Two operations, in order: delete unused materials in one transaction, then `doc.Save(new SaveOptions { Compact = true })` to defragment the `.rfa`.

## Gotchas

- **Used-material collection sweeps two sources, not one.** `Element.GetMaterialIds` (called with both `false` **and** `true`, for non-render + render appearance) over every instance and type covers geometry references — but **subcategory appearance materials are not returned by it**. Those are collected separately by walking `doc.Settings.Categories` and each category's `SubCategories`. Drop that second sweep and materials still referenced by a subcategory appearance get deleted.
- **System materials can't be deleted** — `doc.Delete` throws `InvalidOperationException` on them; caught and skipped so the pass completes.
