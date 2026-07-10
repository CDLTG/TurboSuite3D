#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using TurboSuite.Shared.Models;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.App.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    // CAD Room Source + Region Generation Layers config moved to the TurboName window
    // (TurboSuite.Name.ViewModels.CadRoomSourceConfigViewModel) — consumed only by TurboName.

    private string _wallSconceFamiliesText;
    private string _receptacleFamiliesText;
    private string _electricalVerticalFamiliesText;
    private string _verticalFamiliesText;
    private string _switchFamiliesText;

    // General
    private bool _showCircuitCommentsDialog = true;
    private bool _autoSplitFixtures = true;
    private bool _enableDynamicDriverTags = true;

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

    public Action<bool?> CloseAction { get; set; }

    public SettingsViewModel(FamilyNameSettings familySettings, GeneralSettings generalSettings)
    {
        LoadFrom(familySettings);
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
