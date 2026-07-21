#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using ACadSharp;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Name.Services;
using TurboSuite.Shared.Filters;
using TurboSuite.Shared.Models;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Name.ViewModels;

/// <summary>
/// CAD Room Source configuration, hosted directly in the TurboName window (consumed by nothing but
/// TurboName). Holds the Block-mode fields + the mode toggle, the ACadSharp-backed block/tag discovery, and
/// the "Pick from view" probe. The region-gen (W/D/A) and text-scope (Name/Ht) scopes are driven by the
/// layer-table role tags via <see cref="SetRole"/>/<see cref="HasRole"/> — there are no typed layer boxes or
/// link dropdowns.
/// </summary>
public class CadRoomSourceConfigViewModel : ViewModelBase
{
    private readonly UIDocument _uidoc;
    private readonly Dictionary<string, CadDocument> _docCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _discoveryLoaded;
    private readonly bool _canPick;
    private string _detectedText;

    private bool _isBlockMode = true;
    private bool _isTextMode;
    private string _blockName;
    private string _ceilingHeightTag;
    private string _roomNameLayer;
    private string _ceilingHeightLayer;
    private string _ceilingHeightBlockName;
    private string _ceilingHeightBlockTag;
    private string _regionTypeName;
    private string _roomNameLinkName = "";
    private string _ceilingHeightLinkName = "";

    // Region-gen scopes as file|layer entries (driven by the layer-table W/D/A toggles, no typed boxes).
    private readonly List<string> _wallScopes = new();
    private readonly List<string> _doorScopes = new();
    private readonly List<string> _areaScopes = new();
    // Legacy region-gen scope for any bare (unqualified) entry loaded from old settings; no UI, passed through.
    private string _sourceLinkName = "";

    public bool IsBlockMode
    {
        get => _isBlockMode;
        set { if (SetProperty(ref _isBlockMode, value)) { if (value) IsTextMode = false; OnPropertyChanged(nameof(RoomSourceModeDisplay)); } }
    }

    public bool IsTextMode
    {
        get => _isTextMode;
        set { if (SetProperty(ref _isTextMode, value)) { if (value) IsBlockMode = false; OnPropertyChanged(nameof(RoomSourceModeDisplay)); } }
    }

    /// <summary>Read-only label for the room-source mode. The mode is no longer a user toggle — it's set by
    /// "Pick from view" (block vs. text is auto-detected) and restored from saved settings — so the window shows
    /// it rather than letting the user flip it.</summary>
    public string RoomSourceModeDisplay => IsTextMode ? "Text on Layer" : "Block Attributes";

    public string BlockName
    {
        get => _blockName;
        set { if (SetProperty(ref _blockName, value)) RefreshAvailableTags(); }
    }

    /// <summary>Attribute tags (ordered) whose values concatenate into the room name — no free typing; the user
    /// adds/removes them from the block's attribute pool via the chips control. Pick-only workflow.</summary>
    public ObservableCollection<string> RoomNameTags { get; } = new();

    public string CeilingHeightTag
    {
        get => _ceilingHeightTag;
        set { if (SetProperty(ref _ceilingHeightTag, value)) RefreshUnassignedTags(); }
    }
    public string RoomNameLayer { get => _roomNameLayer; set => SetProperty(ref _roomNameLayer, value); }
    public string CeilingHeightLayer { get => _ceilingHeightLayer; set => SetProperty(ref _ceilingHeightLayer, value); }
    public string CeilingHeightBlockName
    {
        get => _ceilingHeightBlockName;
        set { if (SetProperty(ref _ceilingHeightBlockName, value)) RefreshAvailableCeilingBlockTags(); }
    }
    public string CeilingHeightBlockTag
    {
        get => _ceilingHeightBlockTag;
        set { if (SetProperty(ref _ceilingHeightBlockTag, value)) RefreshUnassignedCeilingBlockTags(); }
    }
    public string RegionTypeName { get => _regionTypeName; set => SetProperty(ref _regionTypeName, value); }

    /// <summary>The project's FilledRegionType names — the dropdown source for <see cref="RegionTypeName"/>. A
    /// region type must already exist (region-gen never creates one), so the user picks from this set instead of
    /// typing a name that might not exist. Populated from the doc; refreshed on dropdown-open.</summary>
    public ObservableCollection<string> RegionTypeNames { get; } = new();

    /// <summary>Which linked DWG supplies room NAMES (set by the Name role tag on a layer row). "" = all links.</summary>
    public string RoomNameLinkName { get => _roomNameLinkName; set => SetProperty(ref _roomNameLinkName, value); }

    /// <summary>Which linked DWG supplies ceiling HEIGHTS (set by the Ht role tag). "" = all links.</summary>
    public string CeilingHeightLinkName { get => _ceilingHeightLinkName; set => SetProperty(ref _ceilingHeightLinkName, value); }

    /// <summary>Full attribute-tag pool of the main (room-name) block — the source for the two block-mode
    /// dropdowns below. Populated by discovery / Pick.</summary>
    public ObservableCollection<string> AvailableTags { get; } = new();

    /// <summary>Unassigned tags = pool − room-name tags − the ceiling-height tag. Both block-mode dropdowns (add
    /// room-name tag, pick ceiling-height tag) offer this same set; each assigned tag shows as a pill instead.</summary>
    public ObservableCollection<string> UnassignedTags { get; } = new();

    /// <summary>Attribute-tag pool of the separate text-mode height block (its own block ⇒ its own pool, no
    /// shared exclusion). Populated by "Pick height block".</summary>
    public ObservableCollection<string> AvailableCeilingBlockTags { get; } = new();

    /// <summary>Height-block pool minus the currently-selected height-block tag — offered by that dropdown; the
    /// selected tag shows as a pill instead (mirrors the block-mode room-name / ceiling-height pattern).</summary>
    public ObservableCollection<string> UnassignedCeilingBlockTags { get; } = new();

    public string DetectedText { get => _detectedText; set => SetProperty(ref _detectedText, value); }

    public ICommand PickFromViewCommand { get; }
    public ICommand PickHeightBlockCommand { get; }
    public ICommand RemoveRoomNameTagCommand { get; }
    // ✕-clear affordances for the single-select dropdowns (they have no blank entry — this is how you un-set).
    public ICommand ClearCeilingHeightTagCommand { get; }
    public ICommand ClearHeightBlockCommand { get; }
    public ICommand ClearHeightBlockTagCommand { get; }

    /// <summary>Raised when the user clicks "Pick from view" — the host ViewModel queues a
    /// <see cref="Services.PickLayerRequest"/> on the shared external event whose pick action is
    /// <see cref="RunPick"/> (which must run in a valid Revit API context).</summary>
    public event Action PickFromViewRequested;

    /// <summary>Raised when the user clicks "Pick height block" (text mode) — same shared-event pick path, but
    /// the action is <see cref="RunHeightBlockPick"/>.</summary>
    public event Action PickHeightBlockRequested;

    public CadRoomSourceConfigViewModel(CadRoomSourceSettings cadSettings, UIDocument uidoc)
    {
        _uidoc = uidoc;
        LoadCadSettings(cadSettings);
        PickFromViewCommand = new RelayCommand(() => PickFromViewRequested?.Invoke(), () => _canPick);
        PickHeightBlockCommand = new RelayCommand(() => PickHeightBlockRequested?.Invoke(), () => _canPick);
        RemoveRoomNameTagCommand = new RelayCommand<string>(RemoveRoomNameTag);
        ClearCeilingHeightTagCommand = new RelayCommand(() => CeilingHeightTag = "");
        ClearHeightBlockTagCommand = new RelayCommand(() => CeilingHeightBlockTag = "");
        ClearHeightBlockCommand = new RelayCommand(() => { CeilingHeightBlockName = ""; CeilingHeightBlockTag = ""; });

        try
        {
            _canPick = _uidoc != null
                && CadLinkResolver.GetLinkedImports(_uidoc.Document, _uidoc.Document.ActiveView).Count > 0;
        }
        catch { _canPick = false; }

        RefreshRegionTypeNames();
    }

    /// <summary>Populate <see cref="RegionTypeNames"/> from the project's FilledRegionTypes (the same set
    /// <see cref="Services.TurboNameApiHandler.ResolveRegionTypeId"/> matches against). If the saved/current
    /// selection isn't a real type, fall back to "Room Region" (else the first type) so the dropdown never shows
    /// a name that would fail at generation. Cheap enough to re-run on every dropdown-open.</summary>
    public void RefreshRegionTypeNames()
    {
        var doc = _uidoc?.Document;
        if (doc == null) return;

        var names = new FilteredElementCollector(doc)
            .OfClass(typeof(FilledRegionType))
            .Cast<FilledRegionType>()
            .Select(t => t.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SyncCollection(RegionTypeNames, names);

        if (!RegionTypeNames.Any(n => string.Equals(n, RegionTypeName, StringComparison.OrdinalIgnoreCase)))
            RegionTypeName =
                RegionTypeNames.FirstOrDefault(n => n.Equals("Room Region", StringComparison.OrdinalIgnoreCase))
                ?? RegionTypeNames.FirstOrDefault()
                ?? RegionTypeName;
    }

    // ── Block-mode attribute-tag assignment (chips + add-dropdown; no free typing) ──

    /// <summary>Append a tag to the ordered room-name list and refresh both dropdowns' candidate pools.</summary>
    public void AddRoomNameTag(string tag)
    {
        tag = (tag ?? "").Trim();
        if (tag.Length == 0 || RoomNameTags.Contains(tag)) return;
        RoomNameTags.Add(tag);
        RefreshUnassignedTags();
        OnPropertyChanged(nameof(RoomNameTags)); // mark the config dirty (collection change isn't auto-observed)
    }

    /// <summary>Remove a tag from the room-name list, returning it to the candidate pools.</summary>
    public void RemoveRoomNameTag(string tag)
    {
        if (tag == null || !RoomNameTags.Remove(tag)) return;
        RefreshUnassignedTags();
        OnPropertyChanged(nameof(RoomNameTags));
    }

    // Recompute the single unassigned-tags pool both block-mode dropdowns offer = tag pool − room-name tags −
    // the ceiling-height tag. Each assigned tag shows as a pill instead. Non-clearing sync so an open dropdown
    // isn't disturbed mid-refresh.
    private void RefreshUnassignedTags()
    {
        var height = (CeilingHeightTag ?? "").Trim();
        SyncCollection(UnassignedTags, AvailableTags.Where(t =>
            !RoomNameTags.Contains(t) && !t.Equals(height, StringComparison.OrdinalIgnoreCase)));
    }

    // Height-block dropdown pool = its block's tags − the selected height-block tag (shown as a pill instead).
    private void RefreshUnassignedCeilingBlockTags()
    {
        var selected = (CeilingHeightBlockTag ?? "").Trim();
        SyncCollection(UnassignedCeilingBlockTags,
            AvailableCeilingBlockTags.Where(t => !t.Equals(selected, StringComparison.OrdinalIgnoreCase)));
    }

    // Reconcile an ObservableCollection to a desired set in place — remove what's gone, append what's new —
    // without a full Clear(), so a ComboBox bound to it keeps its selection through the update.
    private static void SyncCollection(ObservableCollection<string> target, IEnumerable<string> desired)
    {
        var want = desired.ToList();
        for (int i = target.Count - 1; i >= 0; i--)
            if (!want.Contains(target[i], StringComparer.OrdinalIgnoreCase)) target.RemoveAt(i);
        foreach (var t in want)
            if (!target.Contains(t, StringComparer.OrdinalIgnoreCase)) target.Add(t);
    }

    // ── Role-tag scope API (the layer table drives these; there are no typed boxes / link dropdowns) ──

    /// <summary>Is the layer (<paramref name="file"/>,<paramref name="layer"/>) currently tagged with
    /// <paramref name="role"/>? Used to seed each row's toggle state from saved settings.</summary>
    public bool HasRole(LayerRole role, string file, string layer) => role switch
    {
        LayerRole.Wall => HasRegionGen(_wallScopes, file, layer),
        LayerRole.Door => HasRegionGen(_doorScopes, file, layer),
        LayerRole.Area => HasRegionGen(_areaScopes, file, layer),
        LayerRole.Name => MatchesTextScope(_roomNameLinkName, RoomNameLayer, file, layer),
        LayerRole.Height => MatchesTextScope(_ceilingHeightLinkName, CeilingHeightLayer, file, layer),
        _ => false,
    };

    /// <summary>Apply a role toggle from the layer table. Region-gen roles (W/D/A) accumulate file|layer
    /// entries; Name/Ht are single scopes that clicking sets (or clears). Fires a change notification so the
    /// host marks the config dirty.</summary>
    public void SetRole(LayerRole role, string file, string layer, bool on)
    {
        switch (role)
        {
            case LayerRole.Wall: SetRegionGen(_wallScopes, file, layer, on); break;
            case LayerRole.Door: SetRegionGen(_doorScopes, file, layer, on); break;
            case LayerRole.Area: SetRegionGen(_areaScopes, file, layer, on); break;
            case LayerRole.Name:
                RoomNameLayer = on ? layer : "";
                RoomNameLinkName = on ? (file ?? "") : "";
                break;
            case LayerRole.Height:
                CeilingHeightLayer = on ? layer : "";
                CeilingHeightLinkName = on ? (file ?? "") : "";
                break;
        }
        OnPropertyChanged(nameof(RegionTypeName)); // any notify marks the config dirty
    }

    private static string ScopeKey(string file, string layer) =>
        string.IsNullOrEmpty(file) ? layer : $"{file}|{layer}";

    private bool HasRegionGen(List<string> scopes, string file, string layer)
    {
        foreach (var entry in scopes)
        {
            var (entryFile, entryLayer) = CadLinkScope.ParseScopedLayer(entry);
            if (CadLinkScope.MatchesLayer(entryFile, entryLayer, layer, _sourceLinkName, file))
                return true;
        }
        return false;
    }

    private void SetRegionGen(List<string> scopes, string file, string layer, bool on)
    {
        // Drop any entry (qualified or legacy-bare) that matches this row, then add the canonical file|layer.
        scopes.RemoveAll(entry =>
        {
            var (ef, el) = CadLinkScope.ParseScopedLayer(entry);
            return CadLinkScope.MatchesLayer(ef, el, layer, _sourceLinkName, file);
        });
        if (on) scopes.Add(ScopeKey(file, layer));
    }

    // A Name/Ht text scope matches a row when its layer matches AND its link includes the row's file
    // (blank link = all links).
    private static bool MatchesTextScope(string linkName, string scopeLayer, string file, string layer)
        => !string.IsNullOrEmpty(scopeLayer)
           && string.Equals(scopeLayer, layer, StringComparison.OrdinalIgnoreCase)
           && CadLinkScope.Includes(linkName, file);

    /// <summary>
    /// The pick itself (raised on the shared external event as a <see cref="Services.PickLayerRequest"/>, so it
    /// runs in a valid API context — no nested modal): user clicks a room label in the linked CAD; layer comes
    /// from Revit's GraphicsStyle, the location from the pick point, then classify within that layer via
    /// ACadSharp and fill the fields.
    /// </summary>
    public void RunPick()
    {
        if (_uidoc == null) return;
        var doc = _uidoc.Document;

        Reference reference;
        try
        {
            reference = _uidoc.Selection.PickObject(
                Autodesk.Revit.UI.Selection.ObjectType.PointOnElement,
                new ImportInstanceSelectionFilter(_uidoc.Document),
                "Click a room label in the linked CAD");
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return; // Esc — leave everything as-is
        }

        try
        {
            if (doc.GetElement(reference.ElementId) is not ImportInstance import) return;

            string layerName = null;
            var geomObj = import.GetGeometryObjectFromReference(reference);
            if (geomObj != null && doc.GetElement(geomObj.GraphicsStyleId) is GraphicsStyle style)
                layerName = style.GraphicsStyleCategory?.Name;

            if (string.IsNullOrEmpty(layerName))
            {
                DetectedText = "Detected: couldn't resolve a CAD layer here — use the layer list.";
                return;
            }

            if (!CadLinkResolver.TryGetDwgPath(doc, import, out string dwgPath))
            {
                DetectedText = "Detected: linked DWG not found on disk — use the layer list.";
                return;
            }

            string dwgFile = Path.GetFileName(dwgPath);
            var cadDoc = GetOrLoadCadDoc(dwgPath);
            double unitToFeet = CadLinkResolver.GetUnitToFeetFactor(cadDoc.Header.InsUnits);

            XYZ local = import.GetTransform().Inverse.OfPoint(reference.GlobalPoint);
            double dwgX = local.X / unitToFeet;
            double dwgY = local.Y / unitToFeet;

            var result = CadIntrospectionService.ResolveAtPoint(cadDoc, dwgX, dwgY, layerName);
            if (result == null)
            {
                DetectedText = $"Detected: nothing on layer \"{layerName}\" here — use the layer list.";
                return;
            }

            EnsureDiscoveryLoaded();

            if (result.IsBlock)
            {
                IsBlockMode = true;
                bool blockChanged = !string.Equals((BlockName ?? "").Trim(),
                    (result.BlockName ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
                BlockName = result.BlockName; // setter repopulates AvailableTags for the picked block
                // A different block ⇒ the old tag assignments belong to a block that's no longer selected.
                if (blockChanged) { RoomNameTags.Clear(); CeilingHeightTag = ""; }
                // Block carries both name and height — scope both to the picked DWG.
                RoomNameLinkName = dwgFile;
                CeilingHeightLinkName = dwgFile;
                if (AvailableTags.Count == 0 && result.Tags != null)
                    foreach (var t in result.Tags) AvailableTags.Add(t);
                RefreshUnassignedTags();
                OnPropertyChanged(nameof(RoomNameTags));

                string attrs = (result.TagValues != null && result.TagValues.Count > 0)
                    ? "  →  " + string.Join(",  ",
                        result.TagValues.Select(kv =>
                            $"{(string.IsNullOrEmpty(kv.Value) ? "(empty)" : kv.Value)}={kv.Key}"))
                    : "";
                DetectedText = $"Detected: Block \"{result.BlockName}\" on layer \"{layerName}\".{attrs}";
            }
            else
            {
                // Text room name on this layer + link — the Name role tag on the matching row lights up.
                IsTextMode = true;
                RoomNameLayer = layerName;
                RoomNameLinkName = dwgFile;
                DetectedText = $"Detected: Text on layer \"{layerName}\" in {dwgFile}.";
            }
        }
        catch (IOException ex)
        {
            DetectedText = ex.Message;
        }
        catch (Exception)
        {
            DetectedText = "Detected: couldn't read the linked CAD here — use the layer list.";
        }
    }

    /// <summary>Text-mode "Pick height block": click an INSERT in the linked CAD to set the separate
    /// ceiling-height block and load its own attribute-tag pool. Room names still come from the N layer, so this
    /// block's tag stands alone (no shared exclusion). Runs in a valid API context via the shared event.</summary>
    public void RunHeightBlockPick()
    {
        if (_uidoc == null) return;
        var doc = _uidoc.Document;

        Reference reference;
        try
        {
            reference = _uidoc.Selection.PickObject(
                Autodesk.Revit.UI.Selection.ObjectType.PointOnElement,
                new ImportInstanceSelectionFilter(_uidoc.Document),
                "Click the ceiling-height block in the linked CAD");
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return; // Esc — leave everything as-is
        }

        try
        {
            if (doc.GetElement(reference.ElementId) is not ImportInstance import) return;

            string layerName = null;
            var geomObj = import.GetGeometryObjectFromReference(reference);
            if (geomObj != null && doc.GetElement(geomObj.GraphicsStyleId) is GraphicsStyle style)
                layerName = style.GraphicsStyleCategory?.Name;

            if (!CadLinkResolver.TryGetDwgPath(doc, import, out string dwgPath))
            {
                DetectedText = "Height block: linked DWG not found on disk.";
                return;
            }

            var cadDoc = GetOrLoadCadDoc(dwgPath);
            double unitToFeet = CadLinkResolver.GetUnitToFeetFactor(cadDoc.Header.InsUnits);
            XYZ local = import.GetTransform().Inverse.OfPoint(reference.GlobalPoint);
            var result = CadIntrospectionService.ResolveAtPoint(
                cadDoc, local.X / unitToFeet, local.Y / unitToFeet, layerName);

            if (result == null || !result.IsBlock)
            {
                DetectedText = "Height block: that isn't a block — click an INSERT (block reference).";
                return;
            }

            EnsureDiscoveryLoaded();
            bool changed = !string.Equals((CeilingHeightBlockName ?? "").Trim(),
                (result.BlockName ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            CeilingHeightBlockName = result.BlockName; // setter repopulates AvailableCeilingBlockTags
            if (changed) CeilingHeightBlockTag = "";
            if (AvailableCeilingBlockTags.Count == 0 && result.Tags != null)
                foreach (var t in result.Tags) AvailableCeilingBlockTags.Add(t);
            RefreshUnassignedCeilingBlockTags();

            string attrs = (result.TagValues != null && result.TagValues.Count > 0)
                ? "  →  " + string.Join(",  ",
                    result.TagValues.Select(kv =>
                        $"{(string.IsNullOrEmpty(kv.Value) ? "(empty)" : kv.Value)}={kv.Key}"))
                : "";
            DetectedText = $"Height block: \"{result.BlockName}\".{attrs}";
        }
        catch (IOException ex)
        {
            DetectedText = ex.Message;
        }
        catch (Exception)
        {
            DetectedText = "Height block: couldn't read the linked CAD here.";
        }
    }

    /// <summary>Lazily unions layers + block names across every linked import in the active view (first
    /// dropdown open / first pick — not on window open, to avoid a multi-second freeze).</summary>
    public void EnsureDiscoveryLoaded()
    {
        if (_discoveryLoaded) return;
        _discoveryLoaded = true;

        if (_uidoc == null) return;
        var doc = _uidoc.Document;
        var view = doc.ActiveView;

        // Warm the DWG cache for every linked import so the tag pools (read per-block below) resolve. Block
        // names themselves are no longer listed — blocks are chosen only by Pick-from-view, never typed.
        foreach (var import in CadLinkResolver.GetLinkedImports(doc, view))
        {
            if (!CadLinkResolver.TryGetDwgPath(doc, import, out string path)) continue;
            try { GetOrLoadCadDoc(path); }
            catch { /* skip unreadable/locked DWGs */ }
        }

        RefreshAvailableTags();
        RefreshAvailableCeilingBlockTags();
    }

    private CadDocument GetOrLoadCadDoc(string path)
    {
        if (!_docCache.TryGetValue(path, out var cadDoc))
        {
            cadDoc = CadLinkResolver.Load(path);
            _docCache[path] = cadDoc;
        }
        return cadDoc;
    }

    private void RefreshAvailableTags()
    {
        AvailableTags.Clear();
        string block = (BlockName ?? "").Trim();
        if (_discoveryLoaded && block.Length > 0)
        {
            var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cadDoc in _docCache.Values)
                foreach (var t in CadIntrospectionService.GetAttributeTags(cadDoc, block))
                    tags.Add(t);
            foreach (var t in tags) AvailableTags.Add(t);
        }
        RefreshUnassignedTags(); // keep the shared unassigned pool in sync with the tag pool
    }

    private void RefreshAvailableCeilingBlockTags()
    {
        AvailableCeilingBlockTags.Clear();
        string block = (CeilingHeightBlockName ?? "").Trim();
        if (_discoveryLoaded && block.Length > 0)
        {
            var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cadDoc in _docCache.Values)
                foreach (var t in CadIntrospectionService.GetAttributeTags(cadDoc, block))
                    tags.Add(t);
            foreach (var t in tags) AvailableCeilingBlockTags.Add(t);
        }
        RefreshUnassignedCeilingBlockTags();
    }

    private void LoadCadSettings(CadRoomSourceSettings settings)
    {
        IsBlockMode = settings.Mode != "Text";
        IsTextMode = settings.Mode == "Text";
        BlockName = settings.BlockName ?? "";
        RoomNameTags.Clear();
        foreach (var t in settings.RoomNameTags ?? new List<string>()) RoomNameTags.Add(t);
        CeilingHeightTag = settings.CeilingHeightTag ?? "";
        RoomNameLayer = settings.RoomNameLayer ?? "";
        CeilingHeightLayer = settings.CeilingHeightLayer ?? "";
        CeilingHeightBlockName = settings.CeilingHeightBlockName ?? "";
        CeilingHeightBlockTag = settings.CeilingHeightBlockTag ?? "";
        RegionTypeName = settings.RegionTypeName ?? "Room Region";

        _wallScopes.Clear(); _wallScopes.AddRange(settings.WallLayerNames ?? new List<string>());
        _doorScopes.Clear(); _doorScopes.AddRange(settings.DoorLayerNames ?? new List<string>());
        _areaScopes.Clear(); _areaScopes.AddRange(settings.AreaLayerNames ?? new List<string>());

        _sourceLinkName = settings.SourceLinkName ?? "";
        RoomNameLinkName = settings.RoomNameLinkName ?? "";
        CeilingHeightLinkName = settings.CeilingHeightLinkName ?? "";

        // Seed the tag pools from saved assignments so the dropdowns can show their selected values before the
        // (deferred, multi-second) CAD discovery runs. Discovery later unions in the rest of the block's tags.
        foreach (var t in RoomNameTags)
            if (!AvailableTags.Contains(t)) AvailableTags.Add(t);
        if (CeilingHeightTag.Length > 0 && !AvailableTags.Contains(CeilingHeightTag))
            AvailableTags.Add(CeilingHeightTag);
        if (CeilingHeightBlockTag.Length > 0 && !AvailableCeilingBlockTags.Contains(CeilingHeightBlockTag))
            AvailableCeilingBlockTags.Add(CeilingHeightBlockTag);
        RefreshUnassignedTags();
        RefreshUnassignedCeilingBlockTags();
    }

    public CadRoomSourceSettings ToModel() => new()
    {
        Mode = IsTextMode ? "Text" : "Block",
        BlockName = (BlockName ?? "").Trim(),
        RoomNameTags = new List<string>(RoomNameTags),
        CeilingHeightTag = (CeilingHeightTag ?? "").Trim(),
        RoomNameLayer = (RoomNameLayer ?? "").Trim(),
        CeilingHeightLayer = (CeilingHeightLayer ?? "").Trim(),
        CeilingHeightBlockName = (CeilingHeightBlockName ?? "").Trim(),
        CeilingHeightBlockTag = (CeilingHeightBlockTag ?? "").Trim(),
        WallLayerNames = new List<string>(_wallScopes),
        DoorLayerNames = new List<string>(_doorScopes),
        AreaLayerNames = new List<string>(_areaScopes),
        RegionTypeName = (RegionTypeName ?? "Room Region").Trim(),
        SourceLinkName = (_sourceLinkName ?? "").Trim(),
        RoomNameLinkName = (RoomNameLinkName ?? "").Trim(),
        CeilingHeightLinkName = (CeilingHeightLinkName ?? "").Trim()
    };

}
