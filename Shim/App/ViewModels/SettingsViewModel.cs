#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using ACadSharp;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Name.Services;
using TurboSuite.Shared.Filters;
using TurboSuite.Shared.Models;
using TurboSuite.Shared.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.App.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    // CAD Room Source discovery (ACadSharp-backed; replaces hand-typing from AutoCAD)
    private readonly UIDocument _uidoc;
    private readonly Dictionary<string, CadDocument> _docCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _discoveryLoaded;
    private readonly bool _canPick;
    private string _detectedText;

    private string _wallSconceFamiliesText;
    private string _receptacleFamiliesText;
    private string _electricalVerticalFamiliesText;
    private string _verticalFamiliesText;
    private string _switchFamiliesText;

    // General
    private bool _showCircuitCommentsDialog = true;
    private bool _autoSplitFixtures = true;
    private bool _enableDynamicDriverTags = true;

    // CAD Room Source
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
    private string _windowLayerNamesText;
    private string _regionTypeName;

    // Report the actually-loaded assembly version, not the auto-update tracking file. version.txt is
    // written only by the installer/updater (never by a dev post-build deploy), so it goes stale on a
    // build box every time the version bumps; the loaded assembly is always the truth of what's running.
    public string VersionText { get; } =
        $"v{(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0)).ToString(3)}";

    public string WallSconceFamiliesText
    {
        get => _wallSconceFamiliesText;
        set => SetProperty(ref _wallSconceFamiliesText, value);
    }

    public string ReceptacleFamiliesText
    {
        get => _receptacleFamiliesText;
        set => SetProperty(ref _receptacleFamiliesText, value);
    }

    public string ElectricalVerticalFamiliesText
    {
        get => _electricalVerticalFamiliesText;
        set => SetProperty(ref _electricalVerticalFamiliesText, value);
    }

    public string VerticalFamiliesText
    {
        get => _verticalFamiliesText;
        set => SetProperty(ref _verticalFamiliesText, value);
    }

    public string SwitchFamiliesText
    {
        get => _switchFamiliesText;
        set => SetProperty(ref _switchFamiliesText, value);
    }

    public bool IsBlockMode
    {
        get => _isBlockMode;
        set
        {
            if (SetProperty(ref _isBlockMode, value) && value)
                IsTextMode = false;
        }
    }

    public bool IsTextMode
    {
        get => _isTextMode;
        set
        {
            if (SetProperty(ref _isTextMode, value) && value)
                IsBlockMode = false;
        }
    }

    public string BlockName
    {
        get => _blockName;
        set
        {
            if (SetProperty(ref _blockName, value))
                RefreshAvailableTags();
        }
    }

    public string RoomNameTagsText
    {
        get => _roomNameTagsText;
        set => SetProperty(ref _roomNameTagsText, value);
    }

    public string CeilingHeightTag
    {
        get => _ceilingHeightTag;
        set => SetProperty(ref _ceilingHeightTag, value);
    }

    public string RoomNameLayer
    {
        get => _roomNameLayer;
        set => SetProperty(ref _roomNameLayer, value);
    }

    public string CeilingHeightLayer
    {
        get => _ceilingHeightLayer;
        set => SetProperty(ref _ceilingHeightLayer, value);
    }

    public string CeilingHeightBlockName
    {
        get => _ceilingHeightBlockName;
        set => SetProperty(ref _ceilingHeightBlockName, value);
    }

    public string CeilingHeightBlockTag
    {
        get => _ceilingHeightBlockTag;
        set => SetProperty(ref _ceilingHeightBlockTag, value);
    }

    public string WallLayerNamesText
    {
        get => _wallLayerNamesText;
        set => SetProperty(ref _wallLayerNamesText, value);
    }

    public string DoorLayerNamesText
    {
        get => _doorLayerNamesText;
        set => SetProperty(ref _doorLayerNamesText, value);
    }

    public string WindowLayerNamesText
    {
        get => _windowLayerNamesText;
        set => SetProperty(ref _windowLayerNamesText, value);
    }

    public string RegionTypeName
    {
        get => _regionTypeName;
        set => SetProperty(ref _regionTypeName, value);
    }

    public bool ShowCircuitCommentsDialog
    {
        get => _showCircuitCommentsDialog;
        set => SetProperty(ref _showCircuitCommentsDialog, value);
    }

    public bool AutoSplitFixtures
    {
        get => _autoSplitFixtures;
        set => SetProperty(ref _autoSplitFixtures, value);
    }

    public bool EnableDynamicDriverTags
    {
        get => _enableDynamicDriverTags;
        set => SetProperty(ref _enableDynamicDriverTags, value);
    }

    public ICommand SaveCommand { get; }
    public ICommand ResetDefaultsCommand { get; }
    public ICommand PickFromViewCommand { get; }

    public Action<bool?> CloseAction { get; set; }

    /// <summary>
    /// Set by <see cref="PickFromViewCommand"/>. PickObject cannot run reliably while the Settings
    /// dialog's own modal ShowDialog loop is on the stack (the nested modal corrupts the dialog,
    /// crashing Save and killing Cancel). So the command closes the dialog with this flag set;
    /// SettingsCommand then runs <see cref="RunPick"/> in clean context and reopens the dialog bound
    /// to this same ViewModel, preserving all in-progress edits.
    /// </summary>
    public bool PickRequested { get; set; }

    // Discovery ItemsSources (alpha-sorted, exhaustive — the fallback to the pick).
    public ObservableCollection<string> AvailableBlockNames { get; } = new();
    public ObservableCollection<string> AvailableLayers { get; } = new();
    public ObservableCollection<string> AvailableTags { get; } = new();

    /// <summary>Confirmation text under the "Pick from view" button (e.g. Detected: Block "CDA_ROOM"...).</summary>
    public string DetectedText
    {
        get => _detectedText;
        set => SetProperty(ref _detectedText, value);
    }

    public SettingsViewModel(FamilyNameSettings familySettings, CadRoomSourceSettings cadSettings,
        GeneralSettings generalSettings, UIDocument uidoc)
    {
        _uidoc = uidoc;
        LoadFrom(familySettings);
        LoadCadSettings(cadSettings);
        LoadGeneralSettings(generalSettings);
        SaveCommand = new RelayCommand(OnSave);
        ResetDefaultsCommand = new RelayCommand(OnResetDefaults);
        PickFromViewCommand = new RelayCommand(OnPickFromView, () => _canPick);

        // The active view can't change while the modal dialog is up, so resolve "is the button
        // usable" once instead of on every CommandManager requery tick.
        try
        {
            _canPick = _uidoc != null
                && CadLinkResolver.GetLinkedImports(_uidoc.Document, _uidoc.Document.ActiveView).Count > 0;
        }
        catch
        {
            _canPick = false;
        }
    }

    private void OnPickFromView()
    {
        PickRequested = true;
        CloseAction?.Invoke(null); // close (no save); SettingsCommand picks, then reopens this VM
    }

    /// <summary>
    /// The pick itself: user clicks a room label in the linked CAD; we resolve the layer from
    /// Revit's GraphicsStyle and the location from the pick point, then classify within that layer
    /// via ACadSharp and fill the fields. Called by SettingsCommand between dialog showings, so no
    /// modal dialog is on the stack and PickObject behaves normally.
    /// </summary>
    public void RunPick()
    {
        if (_uidoc == null) return;
        var doc = _uidoc.Document;
        var view = doc.ActiveView;

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

            // Layer (Revit's deterministic half): geometry → GraphicsStyle → category name == DWG layer.
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

            // Location: Revit global → import-local feet → DWG units.
            XYZ local = import.GetTransform().Inverse.OfPoint(reference.GlobalPoint);
            double dwgX = local.X / unitToFeet;
            double dwgY = local.Y / unitToFeet;

            var result = CadIntrospectionService.ResolveAtPoint(cadDoc, dwgX, dwgY, layerName);
            if (result == null)
            {
                DetectedText = $"Detected: nothing on layer \"{layerName}\" here — use the dropdowns.";
                return;
            }

            // Load the dropdowns now so the user has the full fallback after a pick.
            EnsureDiscoveryLoaded();

            if (result.IsBlock)
            {
                IsBlockMode = true;
                BlockName = result.BlockName; // setter refreshes AvailableTags from the cached docs
                if (AvailableTags.Count == 0 && result.Tags != null)
                    foreach (var t in result.Tags)
                        AvailableTags.Add(t);

                // Surface the clicked room's tag=value pairs so the user can tell which tag holds
                // the room name vs. the ceiling height by reading the values, then fill the fields.
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
            DetectedText = ex.Message; // friendly "open in AutoCAD" message
        }
        catch (Exception)
        {
            DetectedText = "Detected: couldn't read the linked CAD here — use the dropdowns.";
        }
    }

    /// <summary>
    /// Lazily unions layers + referenced block names across every linked import in the active view,
    /// caching each loaded DWG for the dialog lifetime. Runs on first dropdown open / first pick —
    /// NOT on window open — to avoid a multi-second freeze when the CAD section isn't being touched.
    /// </summary>
    public void EnsureDiscoveryLoaded()
    {
        if (_discoveryLoaded) return;
        _discoveryLoaded = true; // set first so a slow/locked DWG can't re-trigger the load on every retry

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
            catch { continue; } // locked/unreadable — skip; the other imports still populate

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

        foreach (var t in tags)
            AvailableTags.Add(t);
    }

    private void OnSave()
    {
        CloseAction?.Invoke(true);
    }

    private void OnResetDefaults()
    {
        LoadFrom(FamilyNameSettings.CreateDefaults());
        LoadCadSettings(CadRoomSourceSettings.CreateDefaults());
        LoadGeneralSettings(GeneralSettings.CreateDefaults());
    }

    private void LoadFrom(FamilyNameSettings settings)
    {
        WallSconceFamiliesText = string.Join(Environment.NewLine, settings.WallSconceFamilies);
        ReceptacleFamiliesText = string.Join(Environment.NewLine, settings.ReceptacleFamilies);
        ElectricalVerticalFamiliesText = string.Join(Environment.NewLine, settings.ElectricalVerticalFamilies);
        VerticalFamiliesText = string.Join(Environment.NewLine, settings.VerticalFamilies);
        SwitchFamiliesText = string.Join(Environment.NewLine, settings.SwitchFamilies);
    }

    private void LoadGeneralSettings(GeneralSettings settings)
    {
        ShowCircuitCommentsDialog = settings.ShowCircuitCommentsDialog;
        AutoSplitFixtures = settings.AutoSplitFixtures;
        EnableDynamicDriverTags = settings.EnableDynamicDriverTags;
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
        WindowLayerNamesText = string.Join(", ", settings.WindowLayerNames ?? new List<string>());
        RegionTypeName = settings.RegionTypeName ?? "Room Region";
    }

    public FamilyNameSettings ToFamilyModel() => new()
    {
        WallSconceFamilies = ParseLines(WallSconceFamiliesText),
        ReceptacleFamilies = ParseLines(ReceptacleFamiliesText),
        ElectricalVerticalFamilies = ParseLines(ElectricalVerticalFamiliesText),
        VerticalFamilies = ParseLines(VerticalFamiliesText),
        SwitchFamilies = ParseLines(SwitchFamiliesText)
    };

    public GeneralSettings ToGeneralModel() => new()
    {
        ShowCircuitCommentsDialog = ShowCircuitCommentsDialog,
        AutoSplitFixtures = AutoSplitFixtures,
        EnableDynamicDriverTags = EnableDynamicDriverTags
    };

    public CadRoomSourceSettings ToCadModel() => new()
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
        WindowLayerNames = ParseCommaSeparated(WindowLayerNamesText),
        RegionTypeName = (RegionTypeName ?? "Room Region").Trim()
    };

    private static List<string> ParseCommaSeparated(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        return text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static HashSet<string> ParseLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return new HashSet<string>(
            text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0),
            StringComparer.OrdinalIgnoreCase);
    }
}
