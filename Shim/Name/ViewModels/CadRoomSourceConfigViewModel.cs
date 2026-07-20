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
/// CAD Room Source + Region-Generation-Layers configuration, extracted out of the global Settings dialog
/// and hosted directly in the TurboName window (this config is consumed by nothing but TurboName). Owns the
/// ACadSharp-backed discovery dropdowns, the "Pick from view" round-trip, and the linked-DWG source picker.
/// </summary>
public class CadRoomSourceConfigViewModel : ViewModelBase
{
    public const string AllLinksLabel = "(All links)";

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
    private string _wallLayerNamesText;
    private string _doorLayerNamesText;
    private string _areaLayerNamesText;
    private string _regionTypeName;
    private string _selectedSourceLink;

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
    public string WallLayerNamesText { get => _wallLayerNamesText; set => SetProperty(ref _wallLayerNamesText, value); }
    public string DoorLayerNamesText { get => _doorLayerNamesText; set => SetProperty(ref _doorLayerNamesText, value); }
    public string AreaLayerNamesText { get => _areaLayerNamesText; set => SetProperty(ref _areaLayerNamesText, value); }
    public string RegionTypeName { get => _regionTypeName; set => SetProperty(ref _regionTypeName, value); }
    public string SelectedSourceLink { get => _selectedSourceLink; set => SetProperty(ref _selectedSourceLink, value); }

    /// <summary>Linked-DWG file names in the active view, plus the "(All links)" option at the top.</summary>
    public ObservableCollection<string> AvailableSourceLinks { get; } = new();

    public ObservableCollection<string> AvailableBlockNames { get; } = new();
    public ObservableCollection<string> AvailableLayers { get; } = new();
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
        PopulateSourceLinks();
        LoadCadSettings(cadSettings);
        PickFromViewCommand = new RelayCommand(() => PickFromViewRequested?.Invoke(), () => _canPick);

        try
        {
            _canPick = _uidoc != null
                && CadLinkResolver.GetLinkedImports(_uidoc.Document, _uidoc.Document.ActiveView).Count > 0;
        }
        catch { _canPick = false; }
    }

    private void PopulateSourceLinks()
    {
        AvailableSourceLinks.Add(AllLinksLabel);
        if (_uidoc == null) return;
        try
        {
            var doc = _uidoc.Document;
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var import in CadLinkResolver.GetLinkedImports(doc, doc.ActiveView))
                if (CadLinkResolver.TryGetDwgPath(doc, import, out string path))
                    names.Add(Path.GetFileName(path));
            foreach (var n in names) AvailableSourceLinks.Add(n);
        }
        catch { /* leave just the all-links option */ }
    }

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
                DetectedText = "Detected: couldn't resolve a CAD layer here — use the dropdowns.";
                return;
            }

            if (!CadLinkResolver.TryGetDwgPath(doc, import, out string dwgPath))
            {
                DetectedText = "Detected: linked DWG not found on disk — use the dropdowns.";
                return;
            }

            var cadDoc = GetOrLoadCadDoc(dwgPath);
            double unitToFeet = CadLinkResolver.GetUnitToFeetFactor(cadDoc.Header.InsUnits);

            XYZ local = import.GetTransform().Inverse.OfPoint(reference.GlobalPoint);
            double dwgX = local.X / unitToFeet;
            double dwgY = local.Y / unitToFeet;

            var result = CadIntrospectionService.ResolveAtPoint(cadDoc, dwgX, dwgY, layerName);
            if (result == null)
            {
                DetectedText = $"Detected: nothing on layer \"{layerName}\" here — use the dropdowns.";
                return;
            }

            EnsureDiscoveryLoaded();

            if (result.IsBlock)
            {
                IsBlockMode = true;
                BlockName = result.BlockName;
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
                IsTextMode = true;
                RoomNameLayer = layerName;
                DetectedText = $"Detected: Text on layer \"{layerName}\".";
            }
        }
        catch (IOException ex)
        {
            DetectedText = ex.Message;
        }
        catch (Exception)
        {
            DetectedText = "Detected: couldn't read the linked CAD here — use the dropdowns.";
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

        var layers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocks = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var import in CadLinkResolver.GetLinkedImports(doc, view))
        {
            if (!CadLinkResolver.TryGetDwgPath(doc, import, out string path)) continue;
            CadDocument cadDoc;
            try { cadDoc = GetOrLoadCadDoc(path); }
            catch { continue; }
            foreach (var l in CadIntrospectionService.GetLayers(cadDoc)) layers.Add(l);
            foreach (var b in CadIntrospectionService.GetReferencedBlockNames(cadDoc)) blocks.Add(b);
        }

        AvailableLayers.Clear();
        foreach (var l in layers) AvailableLayers.Add(l);
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
        WallLayerNamesText = string.Join(", ", settings.WallLayerNames ?? new List<string>());
        DoorLayerNamesText = string.Join(", ", settings.DoorLayerNames ?? new List<string>());
        AreaLayerNamesText = string.Join(", ", settings.AreaLayerNames ?? new List<string>());
        RegionTypeName = settings.RegionTypeName ?? "Room Region";

        string link = (settings.SourceLinkName ?? "").Trim();
        if (link.Length > 0 && !AvailableSourceLinks.Contains(link, StringComparer.OrdinalIgnoreCase))
            AvailableSourceLinks.Add(link); // preserve a saved link even if it isn't currently in the view
        SelectedSourceLink = link.Length == 0 ? AllLinksLabel : link;
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
        WallLayerNames = ParseCommaSeparated(WallLayerNamesText),
        DoorLayerNames = ParseCommaSeparated(DoorLayerNamesText),
        AreaLayerNames = ParseCommaSeparated(AreaLayerNamesText),
        RegionTypeName = (RegionTypeName ?? "Room Region").Trim(),
        SourceLinkName = (SelectedSourceLink == AllLinksLabel || SelectedSourceLink == null)
            ? "" : SelectedSourceLink.Trim()
    };

    private static List<string> ParseCommaSeparated(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }
}
