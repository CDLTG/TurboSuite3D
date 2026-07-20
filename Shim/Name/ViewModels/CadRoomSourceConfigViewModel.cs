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
    private string _roomNameTagsText;
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
        set { if (SetProperty(ref _isBlockMode, value) && value) IsTextMode = false; }
    }

    public bool IsTextMode
    {
        get => _isTextMode;
        set { if (SetProperty(ref _isTextMode, value) && value) IsBlockMode = false; }
    }

    public string BlockName
    {
        get => _blockName;
        set { if (SetProperty(ref _blockName, value)) RefreshAvailableTags(); }
    }

    public string RoomNameTagsText { get => _roomNameTagsText; set => SetProperty(ref _roomNameTagsText, value); }
    public string CeilingHeightTag { get => _ceilingHeightTag; set => SetProperty(ref _ceilingHeightTag, value); }
    public string RoomNameLayer { get => _roomNameLayer; set => SetProperty(ref _roomNameLayer, value); }
    public string CeilingHeightLayer { get => _ceilingHeightLayer; set => SetProperty(ref _ceilingHeightLayer, value); }
    public string CeilingHeightBlockName { get => _ceilingHeightBlockName; set => SetProperty(ref _ceilingHeightBlockName, value); }
    public string CeilingHeightBlockTag { get => _ceilingHeightBlockTag; set => SetProperty(ref _ceilingHeightBlockTag, value); }
    public string RegionTypeName { get => _regionTypeName; set => SetProperty(ref _regionTypeName, value); }

    /// <summary>Which linked DWG supplies room NAMES (set by the Name role tag on a layer row). "" = all links.</summary>
    public string RoomNameLinkName { get => _roomNameLinkName; set => SetProperty(ref _roomNameLinkName, value); }

    /// <summary>Which linked DWG supplies ceiling HEIGHTS (set by the Ht role tag). "" = all links.</summary>
    public string CeilingHeightLinkName { get => _ceilingHeightLinkName; set => SetProperty(ref _ceilingHeightLinkName, value); }

    public ObservableCollection<string> AvailableBlockNames { get; } = new();
    public ObservableCollection<string> AvailableTags { get; } = new();

    public string DetectedText { get => _detectedText; set => SetProperty(ref _detectedText, value); }

    public ICommand PickFromViewCommand { get; }

    /// <summary>Raised when the user clicks "Pick from view" — the host ViewModel queues a
    /// <see cref="Services.PickLayerRequest"/> on the shared external event whose pick action is
    /// <see cref="RunPick"/> (which must run in a valid Revit API context).</summary>
    public event Action PickFromViewRequested;

    public CadRoomSourceConfigViewModel(CadRoomSourceSettings cadSettings, UIDocument uidoc)
    {
        _uidoc = uidoc;
        LoadCadSettings(cadSettings);
        PickFromViewCommand = new RelayCommand(() => PickFromViewRequested?.Invoke(), () => _canPick);

        try
        {
            _canPick = _uidoc != null
                && CadLinkResolver.GetLinkedImports(_uidoc.Document, _uidoc.Document.ActiveView).Count > 0;
        }
        catch { _canPick = false; }
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
                BlockName = result.BlockName;
                // Block carries both name and height — scope both to the picked DWG.
                RoomNameLinkName = dwgFile;
                CeilingHeightLinkName = dwgFile;
                if (AvailableTags.Count == 0 && result.Tags != null)
                    foreach (var t in result.Tags) AvailableTags.Add(t);

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

    /// <summary>Lazily unions layers + block names across every linked import in the active view (first
    /// dropdown open / first pick — not on window open, to avoid a multi-second freeze).</summary>
    public void EnsureDiscoveryLoaded()
    {
        if (_discoveryLoaded) return;
        _discoveryLoaded = true;

        if (_uidoc == null) return;
        var doc = _uidoc.Document;
        var view = doc.ActiveView;

        var blocks = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var import in CadLinkResolver.GetLinkedImports(doc, view))
        {
            if (!CadLinkResolver.TryGetDwgPath(doc, import, out string path)) continue;
            CadDocument cadDoc;
            try { cadDoc = GetOrLoadCadDoc(path); }
            catch { continue; }
            foreach (var b in CadIntrospectionService.GetReferencedBlockNames(cadDoc)) blocks.Add(b);
        }

        AvailableBlockNames.Clear();
        foreach (var b in blocks) AvailableBlockNames.Add(b);
        RefreshAvailableTags();
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
        if (!_discoveryLoaded) return;

        string block = (BlockName ?? "").Trim();
        if (block.Length == 0) return;

        var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cadDoc in _docCache.Values)
            foreach (var t in CadIntrospectionService.GetAttributeTags(cadDoc, block))
                tags.Add(t);
        foreach (var t in tags) AvailableTags.Add(t);
    }

    private void LoadCadSettings(CadRoomSourceSettings settings)
    {
        IsBlockMode = settings.Mode != "Text";
        IsTextMode = settings.Mode == "Text";
        BlockName = settings.BlockName ?? "";
        RoomNameTagsText = string.Join(", ", settings.RoomNameTags ?? new List<string>());
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
    }

    public CadRoomSourceSettings ToModel() => new()
    {
        Mode = IsTextMode ? "Text" : "Block",
        BlockName = (BlockName ?? "").Trim(),
        RoomNameTags = ParseCommaSeparated(RoomNameTagsText),
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

    private static List<string> ParseCommaSeparated(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }
}
