#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using TurboSuite.Shared.Models;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.App.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private string _wallSconceFamiliesText;
    private string _receptacleFamiliesText;
    private string _electricalVerticalFamiliesText;
    private string _verticalFamiliesText;
    private string _switchFamiliesText;

    // General
    private bool _showCircuitCommentsDialog = true;
    private bool _autoSplitFixtures = true;

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
        set => SetProperty(ref _blockName, value);
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

    public ICommand SaveCommand { get; }
    public ICommand ResetDefaultsCommand { get; }

    public Action<bool?> CloseAction { get; set; }

    public SettingsViewModel(FamilyNameSettings familySettings, CadRoomSourceSettings cadSettings, GeneralSettings generalSettings)
    {
        LoadFrom(familySettings);
        LoadCadSettings(cadSettings);
        LoadGeneralSettings(generalSettings);
        SaveCommand = new RelayCommand(OnSave);
        ResetDefaultsCommand = new RelayCommand(OnResetDefaults);
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
        AutoSplitFixtures = AutoSplitFixtures
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
