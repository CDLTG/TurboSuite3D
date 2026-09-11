#nullable disable
using System;
using System.Reflection;
using System.Windows.Input;
using TurboSuite.Shared.Models;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.App.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    // CAD Room Source + Region Generation Layers config moved to the TurboName window
    // (TurboSuite.Name.ViewModels.CadRoomSourceConfigViewModel) — consumed only by TurboName.
    // Family-name classification settings are gone — families now carry a "TurboSuite Role" type
    // parameter (see Core Roles), so classification lives in the families, not this dialog.

    // General
    private bool _showCircuitCommentsDialog = true;
    private bool _autoSplitFixtures = true;
    private bool _enableDynamicDriverTags = true;

    // Report the actually-loaded assembly version, not the auto-update tracking file. version.txt is
    // written only by the installer/updater (never by a dev post-build deploy), so it goes stale on a
    // build box every time the version bumps; the loaded assembly is always the truth of what's running.
    public string VersionText { get; } =
        $"v{(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0)).ToString(3)}";

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

    public SettingsViewModel(GeneralSettings generalSettings)
    {
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
        LoadGeneralSettings(GeneralSettings.CreateDefaults());
    }

    private void LoadGeneralSettings(GeneralSettings settings)
    {
        ShowCircuitCommentsDialog = settings.ShowCircuitCommentsDialog;
        AutoSplitFixtures = settings.AutoSplitFixtures;
        EnableDynamicDriverTags = settings.EnableDynamicDriverTags;
    }

    public GeneralSettings ToGeneralModel() => new()
    {
        ShowCircuitCommentsDialog = ShowCircuitCommentsDialog,
        AutoSplitFixtures = AutoSplitFixtures,
        EnableDynamicDriverTags = EnableDynamicDriverTags
    };
}
