#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using TurboSuite.Shared.Services;
using TurboSuite.Shared.Storage;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;

namespace TurboSuite.Spike;

/// <summary>
/// TurboSpike — the throwaway diagnostic bench.
///
/// PURPOSE: an always-available (in dev) ribbon button whose <see cref="Execute"/> body is meant to be
/// SWAPPED per-investigation. When you need to know something about the running model before writing
/// targeted code — what a parameter's StorageType actually is, whether an API member exists on this
/// Revit version, what a family's connectors/geometry look like — drop a probe here, build, and read
/// the dialog.
///
/// STATE: rides the shared <c>ExperimentalCommandsEnabled</c> gate in <see cref="App.TurboSuiteApplication"/>,
/// so it surfaces every dev session and is gated off in shipped builds — it never reaches production users.
///
/// CURRENT PROBE — ExtensibleStorage schema split (plan <c>fizzy-enchanting-meteor</c>), now running through
/// the real guard for the post-fix tests (T6–T17). Throwaway "builds" picked at runtime instead of swapping
/// DLLs (the probe schemas never touch a production store):
/// <list type="bullet">
/// <item><b>Gen1</b> — GUID A, field <c>Value</c>; saves as storage generation <see cref="StorageGeneration.Current"/>.</item>
/// <item><b>Gen2</b> — GUID B (a replacement GUID), fields <c>Value</c> + <c>Extra</c>; saves as generation
/// Current + 1 (so it stamps), and reads A as legacy via carry-forward.</item>
/// <item><b>Gen2′</b> — GUID A <i>with</i> <c>Extra</c> (same GUID, changed fields — T4).</item>
/// <item><b>Sibling</b> — GUID A plus a sibling schema S (<c>Extra</c>) on the same element, no bump (T11).</item>
/// </list>
/// Every probe write goes through <see cref="StorageGenerationService.AuthorizeWrite(Document, string, int)"/>.
/// <c>Schema.Lookup</c> caches per Revit session. Use detached copies (or a template copy) only.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class SpikeCommand : IExternalCommand
{
    private static readonly Guid GuidA = new("aca8d095-e7a2-4fdf-af83-f35b33a810f7");
    private static readonly Guid GuidB = new("f55c65d4-06b6-400e-bf37-91d47df876c3");
    private static readonly Guid GuidSibling = new("0a79fda6-e887-4f6b-9c85-ae624c599b11");
    private static readonly Guid StampGuid = new("9ef91f83-68e1-47ba-9f29-2a05e34b0abe");
    private static readonly Guid DmxGuid = new("e5f6a7b8-c9d0-1234-efab-456789012abc");
    private const string ValueField = "Value";
    private const string ExtraField = "Extra";

    private enum Gen { Gen1, Gen2, Gen2Prime }

    /// <summary>Every TurboSuite schema GUID ever shipped, from git history — "current" or "legacy".</summary>
    private static readonly Dictionary<Guid, string> Known = new()
    {
        // Current
        [new("c3d4e5f6-a7b8-9012-cdef-234567890abe")] = "GeneralSettings (current)",
        [new("f1e2d3c4-b5a6-4789-9abc-de0123456789")] = "CadRoomSource (current)",
        [new("a1f3b7c2-4d6e-4a8b-9c0d-2e5f7a8b1c3d")] = "RoomOrder (current)",
        [new("f4c8d1a6-9b2e-4f73-a5d0-6c1e8b4a2d9f")] = "RoomLocation (current)",
        [new("1e1e5492-69a2-4ea0-b911-0c0ce104a17e")] = "RoomOverrides lighting (current)",
        [new("36d2d485-86e4-47a4-a553-3a367f24343c")] = "RoomOverrides shade (current)",
        [new("a7b3e9c1-2d4f-4b6a-9e8c-1f0d3a5b7c9e")] = "ZonesPanelSettings (current)",
        [new("3f8c1d26-5a74-4e39-b2c8-6d9af0147b53")] = "ZonesOneLineView (current)",
        [new("b2e4c8d3-5f7a-4b9c-8d1e-3f6a9b0c2d4e")] = "CircuitNaming KeypadSort (current)",
        [new("c3d5e9f4-6a8b-4c0d-9e2f-4a7b0c1d3e5f")] = "CircuitNaming PrefixSuffix (current)",
        [new("a1b2c3d4-e5f6-4711-8a2b-9c0d1e2f3a4b")] = "WorkbookPath (current)",
        [new("e5f6a7b8-c9d0-1234-efab-456789012abc")] = "Dmx (current)",
        [new("26ac35a5-dcb9-489a-aec5-00e7b5ff0412")] = "Dali (current)",
        [new("6bd83666-ecc0-42b0-a8d8-186b7b37e555")] = "DriverLinks (current)",
        [new("d4e5f6a7-b8c9-0123-defa-3456789012ab")] = "RpsDeferral (current, per-circuit)",
        [new("5c0b8d2e-7a41-4f6e-9b3d-2e8f1a6c4d70")] = "ModuleLock (current)",
        // Legacy — stranded by past GUID bumps
        [new("c3d4e5f6-a7b8-9012-cdef-234567890abc")] = "GeneralSettings (LEGACY)",
        [new("c3d4e5f6-a7b8-9012-cdef-234567890abd")] = "GeneralSettings (LEGACY)",
        [new("b2c3d4e5-f6a7-8901-bcde-f12345678901")] = "CadRoomSource (LEGACY)",
        [new("b2c3d4e5-f6a7-8901-bcde-f12345678902")] = "CadRoomSource (LEGACY)",
        [new("b2c3d4e5-f6a7-8901-bcde-f12345678903")] = "CadRoomSource (LEGACY)",
        [new("c4e8a2d1-7f3b-4c9e-b5d6-1a2e3f4b5c6d")] = "ZonesPanelSettings (LEGACY)",
        [new("d5f9b3e2-8a4c-5daf-c6e7-2b3f4a5d6e7f")] = "ZonesPanelSettings (LEGACY)",
        [new("e6a0c4f3-9b5d-6ebf-d7f8-3c4a5b6e7f80")] = "ZonesPanelSettings (LEGACY)",
        [new("a1b2c3d4-5e6f-7a8b-9c0d-1e2f3a4b5c6d")] = "ZonesPanelSettings (LEGACY)",
        [new("b2c3d4e5-6f7a-8b9c-0d1e-2f3a4b5c6d7e")] = "ZonesPanelSettings (LEGACY)",
        [new("f4a1c2d3-5e6f-4a7b-8c9d-0e1f2a3b4c5d")] = "ZonesPanelSettings (LEGACY)",
        // Guard
        [new("9ef91f83-68e1-47ba-9f29-2a05e34b0abe")] = "StorageGeneration STAMP",
        // Probe
        [GuidA] = "Spike probe A",
        [GuidB] = "Spike probe B",
        [GuidSibling] = "Spike probe A-sibling",
    };

    private Document _doc;
    private string _revitVersion;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        _doc = commandData.Application.ActiveUIDocument?.Document;
        if (_doc == null)
        {
            TaskDialog.Show("TurboSpike", "No active document.");
            return Result.Cancelled;
        }
        _revitVersion = commandData.Application.Application.VersionNumber;

        BuildWindow().ShowDialog();
        // Succeeded, always: Cancelled/Failed would discard the probe's committed writes.
        return Result.Succeeded;
    }

    // ── UI ──

    private TextBox _value;
    private TextBox _output;

    private Window BuildWindow()
    {
        var w = new Window
        {
            Title = "TurboSpike — ES split probe",
            Width = 1000, Height = 720,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var root = new Grid { Margin = new Thickness(10) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        valueRow.Children.Add(new TextBlock { Text = "Value to write:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _value = new TextBox { Width = 240, Text = "alpha" };
        valueRow.Children.Add(_value);
        Grid.SetRow(valueRow, 0);
        root.Children.Add(valueRow);

        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        void Add(string label, Action a)
        {
            var b = new Button { Content = label, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6) };
            b.Click += (_, _) =>
            {
                try { a(); }
                catch (Exception ex) { Show($"UNHANDLED {ex.GetType().Name}: {ex.Message}"); }
            };
            buttons.Children.Add(b);
        }
        Add("Inventory", Inventory);
        Add("Gen1 Write", () => Write(Gen.Gen1));
        Add("Gen1 Read", () => Read(Gen.Gen1));
        Add("Gen2 Write", () => Write(Gen.Gen2));
        Add("Gen2 Read", () => Read(Gen.Gen2));
        Add("Gen2′ Write (GUID A + Extra)", () => Write(Gen.Gen2Prime));
        Add("Gen2′ Read", () => Read(Gen.Gen2Prime));
        Add("Sibling Write (A + S)", SiblingWrite);
        Add("Sibling Read", SiblingRead);
        Add("Delete probe-A stores (T12)", () => DeleteByGuid(GuidA, "probe A"));
        Add("Un-stamp (T12)", () => DeleteByGuid(StampGuid, "stamp"));
        Add("Corrupt DMX payload (T15)", () => PokeDmx(corrupt: true));
        Add("DMX PayloadVersion → 99 (T15)", () => PokeDmx(corrupt: false));
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);

        _output = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = "Pick an action. Output is selectable — Ctrl+A, Ctrl+C to paste it back."
        };
        Grid.SetRow(_output, 2);
        root.Children.Add(_output);

        w.Content = root;
        return w;
    }

    private void Show(string text) => _output.Text = text;

    // ── Probe schemas ──

    private static Guid GuidOf(Gen g) => g == Gen.Gen2 ? GuidB : GuidA;
    private static bool HasExtra(Gen g) => g != Gen.Gen1;
    private static string Label(Gen g) => g switch { Gen.Gen1 => "Gen1", Gen.Gen2 => "Gen2", _ => "Gen2′" };

    private static Schema GetOrCreate(Gen g, StringBuilder sb)
    {
        var guid = GuidOf(g);
        var schema = Schema.Lookup(guid);
        if (schema != null)
        {
            sb.AppendLine($"Schema.Lookup({Short(guid)}) → existing in session: {schema.SchemaName} [{FieldList(schema)}]");
            return schema;
        }
        var b = new SchemaBuilder(guid);
        b.SetSchemaName(g == Gen.Gen2 ? "TurboSpikeProbeB" : "TurboSpikeProbeA");
        b.SetReadAccessLevel(AccessLevel.Public);
        b.SetWriteAccessLevel(AccessLevel.Public);
        b.AddSimpleField(ValueField, typeof(string));
        if (HasExtra(g)) b.AddSimpleField(ExtraField, typeof(string));
        schema = b.Finish();
        sb.AppendLine($"Schema.Lookup({Short(guid)}) → null; built: {schema.SchemaName} [{FieldList(schema)}]");
        return schema;
    }

    // ── Actions ──

    private void Write(Gen g)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Label(g)} WRITE  \"{_value.Text}\"   ({Header()})");
        sb.AppendLine();

        Schema schema;
        try { schema = GetOrCreate(g, sb); }
        catch (Exception ex)
        {
            sb.AppendLine($"SchemaBuilder/Lookup THREW {ex.GetType().FullName}: {ex.Message}");
            Show(sb.ToString());
            return;
        }

        int buildGen = BuildGen(g);
        using var tx = new Transaction(_doc, $"TurboSpike - {Label(g)} write");
        tx.Start();
        try
        {
            var before = StorageGenerationService.Read(_doc);
            sb.AppendLine($"Guard: doc generation {before.Generation}, acting as build generation {buildGen}");
            if (!StorageGenerationService.AuthorizeWrite(_doc, "spike probe", buildGen))
            {
                tx.RollBack();
                sb.AppendLine("BLOCKED by the storage-generation guard — nothing written.");
                sb.AppendLine();
                AppendProbeStores(sb);
                Show(sb.ToString());
                return;
            }

            var existing = DataStorageHelper.FindDataStorage(_doc, schema);
            var storage = existing ?? DataStorage.Create(_doc);
            sb.AppendLine(existing != null
                ? $"Store: existing DataStorage {storage.Id}"
                : $"Store: none found → created DataStorage {storage.Id}");

            var entity = new Entity(schema);
            entity.Set(ValueField, _value.Text ?? "");
            if (HasExtra(g))
            {
                try
                {
                    entity.Set(ExtraField, "extra:" + (_value.Text ?? ""));
                    sb.AppendLine("Set(Extra) ok");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Set(Extra) THREW {ex.GetType().FullName}: {ex.Message}");
                }
            }
            storage.SetEntity(entity);
            bool ok = CommitGuard.TryCommit(tx);
            sb.AppendLine(ok ? "Commit: Committed" : "Commit: ROLLED BACK (worksharing denial?)");
            var after = StorageGenerationService.Read(_doc);
            sb.AppendLine($"Doc generation now {after.Generation}" + (after.Generation != before.Generation ? $"  (STAMPED by {after.WrittenBy})" : "  (no stamp written)"));
        }
        catch (Exception ex)
        {
            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
            sb.AppendLine($"Write THREW {ex.GetType().FullName}: {ex.Message}");
        }

        sb.AppendLine();
        AppendProbeStores(sb);
        Show(sb.ToString());
    }

    private void Read(Gen g)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Label(g)} READ   ({Header()})");
        sb.AppendLine();

        if (g == Gen.Gen2)
        {
            var (store, used, choice) = DataStorageHelper.FindCurrentOrLegacy(_doc, GuidB, new[] { GuidA });
            sb.AppendLine($"Carry-forward choice: {choice.Source}" + (choice.Source == StoreSource.Legacy ? " (legacy A, mapped)" : ""));
            if (store != null)
            {
                var e = store.GetEntity(used);
                sb.AppendLine($"Reads DataStorage {store.Id} ({used.SchemaName}):");
                sb.AppendLine($"  Value = {SafeString(e, used, ValueField)}");
                sb.AppendLine($"  Extra = {(choice.Source == StoreSource.Legacy ? "(default — A has no Extra)" : SafeString(e, used, ExtraField))}");
            }
            sb.AppendLine();
            AppendProbeStores(sb);
            Show(sb.ToString());
            return;
        }

        var guid = GuidOf(g);
        var schema = Schema.Lookup(guid);
        if (schema == null)
        {
            sb.AppendLine($"Schema.Lookup({Short(guid)}) → null  ⇒  EMPTY (this build sees nothing)");
        }
        else
        {
            sb.AppendLine($"Schema.Lookup({Short(guid)}) → {schema.SchemaName} [{FieldList(schema)}]");
            var storage = DataStorageHelper.FindDataStorage(_doc, schema);
            if (storage == null) sb.AppendLine("No DataStorage with this schema  ⇒  EMPTY");
            else
            {
                var e = storage.GetEntity(schema);
                sb.AppendLine($"Reads DataStorage {storage.Id} (lowest id):");
                sb.AppendLine($"  Value = {SafeString(e, schema, ValueField)}");
                if (HasExtra(g)) sb.AppendLine($"  Extra = {SafeString(e, schema, ExtraField)}");
            }
        }

        sb.AppendLine();
        AppendProbeStores(sb);
        Show(sb.ToString());
    }

    private void Inventory()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"INVENTORY   ({Header()})");
        sb.AppendLine($"Title: {_doc.Title}");
        sb.AppendLine($"Path:  {Safe(() => _doc.PathName)}");
        sb.AppendLine($"Workshared: {Safe(() => _doc.IsWorkshared.ToString())}   Detached: {Safe(() => _doc.IsDetached.ToString())}   " +
                      $"Template (.rte): {StorageGenerationGate.IsTemplatePath(Safe(() => _doc.PathName))}");
        var (gen, by) = StorageGenerationService.Read(_doc);
        sb.AppendLine($"Storage generation: {gen}{(by != null ? $"  (highest stamp by {by})" : "  (unstamped)")}");
        sb.AppendLine();

        var stores = new FilteredElementCollector(_doc).OfClass(typeof(DataStorage)).Cast<DataStorage>()
            .OrderBy(d => d.Id.ToString().Length).ThenBy(d => d.Id.ToString()).ToList();
        sb.AppendLine($"{stores.Count} DataStorage element(s):");

        var perSchema = new Dictionary<Guid, int>();
        foreach (var ds in stores)
        {
            IList<Guid> guids;
            try { guids = ds.GetEntitySchemaGuids(); }
            catch (Exception ex) { sb.AppendLine($"  [{ds.Id}] GetEntitySchemaGuids threw: {ex.Message}"); continue; }

            if (guids.Count == 0) { sb.AppendLine($"  [{ds.Id}] (no entities)"); continue; }
            foreach (var g in guids)
            {
                perSchema[g] = perSchema.TryGetValue(g, out var n) ? n + 1 : 1;
                var schema = Schema.Lookup(g);
                string label = Known.TryGetValue(g, out var k) ? k : "UNKNOWN (not TurboSuite, or unrecorded)";
                sb.AppendLine($"  [{ds.Id}] {label}");
                sb.AppendLine($"        {g}  name={schema?.SchemaName ?? "(not loaded)"}  vendor={Safe(() => schema?.VendorId)}");
                if (schema == null) continue;
                sb.AppendLine($"        fields: {FieldList(schema)}");
                foreach (var line in FieldValues(ds, schema)) sb.AppendLine($"        {line}");
            }
        }

        var dupes = perSchema.Where(kv => kv.Value > 1).ToList();
        sb.AppendLine();
        sb.AppendLine(dupes.Count == 0
            ? "No schema appears on more than one DataStorage."
            : "DUPLICATES (same schema on several DataStorage): " +
              string.Join(", ", dupes.Select(kv => $"{(Known.TryGetValue(kv.Key, out var k) ? k : Short(kv.Key))} ×{kv.Value}")));
        Show(sb.ToString());
    }

    private static int BuildGen(Gen g) => g == Gen.Gen2 ? StorageGeneration.Current + 1 : StorageGeneration.Current;

    private static Schema GetOrCreateSibling()
    {
        var schema = Schema.Lookup(GuidSibling);
        if (schema != null) return schema;
        var b = new SchemaBuilder(GuidSibling);
        b.SetSchemaName("TurboSpikeProbeASibling");
        b.SetReadAccessLevel(AccessLevel.Public);
        b.SetWriteAccessLevel(AccessLevel.Public);
        b.AddSimpleField(ExtraField, typeof(string));
        return b.Finish();
    }

    /// <summary>T11: the original A (Value) + a sibling schema S (Extra) on the <b>same</b> DataStorage, no bump.</summary>
    private void SiblingWrite()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SIBLING WRITE  \"{_value.Text}\"   ({Header()})");
        var a = GetOrCreate(Gen.Gen1, sb);
        var sib = GetOrCreateSibling();

        using var tx = new Transaction(_doc, "TurboSpike - sibling write");
        tx.Start();
        if (!StorageGenerationService.AuthorizeWrite(_doc, "spike probe", StorageGeneration.Current))
        {
            tx.RollBack();
            sb.AppendLine("BLOCKED by the storage-generation guard — nothing written.");
            Show(sb.ToString());
            return;
        }
        var storage = DataStorageHelper.FindDataStorage(_doc, a) ?? DataStorage.Create(_doc);
        var ea = new Entity(a);
        ea.Set(ValueField, _value.Text ?? "");
        storage.SetEntity(ea);
        var es = new Entity(sib);
        es.Set(ExtraField, "sib:" + (_value.Text ?? ""));
        storage.SetEntity(es);
        sb.AppendLine($"DataStorage {storage.Id}: A.Value + S.Extra → " + (CommitGuard.TryCommit(tx) ? "Committed" : "ROLLED BACK"));
        sb.AppendLine();
        AppendProbeStores(sb);
        Show(sb.ToString());
    }

    private void SiblingRead()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SIBLING READ   ({Header()})");
        var a = Schema.Lookup(GuidA);
        var storage = a == null ? null : DataStorageHelper.FindDataStorage(_doc, a);
        if (storage == null) sb.AppendLine("No probe-A store  ⇒  EMPTY");
        else
        {
            var sib = Schema.Lookup(GuidSibling);
            var es = sib == null ? null : storage.GetEntity(sib);
            sb.AppendLine($"DataStorage {storage.Id}:");
            sb.AppendLine($"  A.Value = {SafeString(storage.GetEntity(a), a, ValueField)}");
            sb.AppendLine($"  S.Extra = {(es != null && es.IsValid() ? SafeString(es, sib, ExtraField) : "(sibling absent → default)")}");
        }
        sb.AppendLine();
        AppendProbeStores(sb);
        Show(sb.ToString());
    }

    /// <summary>T12: the narrowed recovery recipe — delete only DataStorage carrying one named GUID.</summary>
    private void DeleteByGuid(Guid guid, string label)
    {
        var sb = new StringBuilder();
        var ids = new FilteredElementCollector(_doc).OfClass(typeof(DataStorage)).Cast<DataStorage>()
            .Where(ds => ds.GetEntitySchemaGuids().Contains(guid)).Select(ds => ds.Id).ToList();
        sb.AppendLine($"DELETE {label} stores ({guid}): {ids.Count} found");
        if (ids.Count > 0)
        {
            using var tx = new Transaction(_doc, $"TurboSpike - delete {label} stores");
            tx.Start();
            _doc.Delete(ids);
            sb.AppendLine(CommitGuard.TryCommit(tx) ? $"Deleted: {string.Join(", ", ids)}" : "ROLLED BACK");
        }
        sb.AppendLine($"Storage generation now {StorageGenerationService.Read(_doc).Generation}");
        Show(sb.ToString());
    }

    /// <summary>T15: tamper with the real DMX store, bypassing the guard (test copy only). Corrupt = unparseable
    /// JSON (the next TurboDMX save must be refused); else keep the JSON and set PayloadVersion 99 (must save).</summary>
    private void PokeDmx(bool corrupt)
    {
        var sb = new StringBuilder();
        var schema = Schema.Lookup(DmxGuid);
        var storage = schema == null ? null : DataStorageHelper.FindDataStorage(_doc, schema);
        if (storage == null) { Show("No TurboDMX store in this project — open TurboDMX and make one edit first."); return; }

        var old = storage.GetEntity(schema);
        string json = old.Get<string>("StateJson") ?? "";
        using var tx = new Transaction(_doc, "TurboSpike - poke DMX payload");
        tx.Start();
        var e = new Entity(schema);
        e.Set("StateJson", corrupt ? "{ not json" + json : json);
        e.Set("PayloadVersion", corrupt ? old.Get<int>("PayloadVersion") : 99);
        storage.SetEntity(e);
        sb.AppendLine((corrupt ? "CORRUPTED DMX payload" : "DMX PayloadVersion → 99 (JSON unchanged)") +
                      $" on DataStorage {storage.Id}: " + (CommitGuard.TryCommit(tx) ? "Committed" : "ROLLED BACK"));
        sb.AppendLine($"Stored JSON length {json.Length}. Now reopen TurboDMX and make an edit.");
        Show(sb.ToString());
    }

    // ── Helpers ──

    private string Header() => $"Revit {_revitVersion}, StorageGeneration.Current={StorageGeneration.Current}";

    private void AppendProbeStores(StringBuilder sb)
    {
        sb.AppendLine("Probe stores in this document:");
        bool any = false;
        foreach (var ds in new FilteredElementCollector(_doc).OfClass(typeof(DataStorage)).Cast<DataStorage>())
        {
            foreach (var g in ds.GetEntitySchemaGuids().Where(g => g == GuidA || g == GuidB || g == GuidSibling))
            {
                any = true;
                var schema = Schema.Lookup(g);
                var e = schema == null ? null : ds.GetEntity(schema);
                sb.AppendLine($"  [{ds.Id}] {Known[g]}  Value={SafeString(e, schema, ValueField)}  Extra={SafeString(e, schema, ExtraField)}");
            }
        }
        if (!any) sb.AppendLine("  (none)");
    }

    private static IEnumerable<string> FieldValues(DataStorage ds, Schema schema)
    {
        Entity e;
        try { e = ds.GetEntity(schema); }
        catch (Exception ex) { return new[] { $"(GetEntity threw: {ex.Message})" }; }
        if (e == null || !e.IsValid()) return new[] { "(entity invalid)" };

        var lines = new List<string>();
        foreach (var f in schema.ListFields())
        {
            string v;
            try { v = Describe(e, f); }
            catch (Exception ex) { v = $"? ({ex.GetType().Name})"; }
            lines.Add($"{f.FieldName} = {v}");
        }
        return lines;
    }

    private static string Describe(Entity e, Field f)
    {
        var t = f.ValueType;
        switch (f.ContainerType)
        {
            case ContainerType.Simple:
                if (t == typeof(string))
                {
                    var s = e.Get<string>(f) ?? "";
                    return s.Length > 80 ? $"\"{s.Substring(0, 80)}…\" ({s.Length} chars)" : $"\"{s}\"";
                }
                if (t == typeof(int)) return e.Get<int>(f).ToString();
                if (t == typeof(bool)) return e.Get<bool>(f).ToString();
                if (t == typeof(Guid)) return e.Get<Guid>(f).ToString();
                if (t == typeof(ElementId)) return e.Get<ElementId>(f)?.ToString() ?? "null";
                return $"({t.Name})";
            case ContainerType.Array:
                if (t == typeof(string)) return $"[{e.Get<IList<string>>(f)?.Count ?? 0} strings]";
                if (t == typeof(ElementId)) return $"[{e.Get<IList<ElementId>>(f)?.Count ?? 0} ids]";
                if (t == typeof(int)) return $"[{e.Get<IList<int>>(f)?.Count ?? 0} ints]";
                return $"[array of {t.Name}]";
            default:
                return $"(map of {t.Name})";
        }
    }

    private static string SafeString(Entity e, Schema schema, string field)
    {
        if (e == null || schema == null || !e.IsValid()) return "(none)";
        if (schema.GetField(field) == null) return "(field not in schema)";
        try { return $"\"{e.Get<string>(field)}\""; }
        catch (Exception ex) { return $"(Get threw {ex.GetType().Name}: {ex.Message})"; }
    }

    private static string FieldList(Schema s) =>
        string.Join(", ", s.ListFields().Select(f => f.FieldName));

    private static string Short(Guid g) => g.ToString().Substring(0, 8);

    private static string Safe(Func<string> f)
    {
        try { return f() ?? ""; }
        catch (Exception ex) { return $"? ({ex.GetType().Name})"; }
    }
}
