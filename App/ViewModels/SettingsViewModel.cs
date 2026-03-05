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

    public ICommand SaveCommand { get; }
    public ICommand ResetDefaultsCommand { get; }

    public Action<bool?> CloseAction { get; set; }

    public SettingsViewModel(FamilyNameSettings settings)
    {
        LoadFrom(settings);
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
    }

    private void LoadFrom(FamilyNameSettings settings)
    {
        WallSconceFamiliesText = string.Join(Environment.NewLine, settings.WallSconceFamilies);
        ReceptacleFamiliesText = string.Join(Environment.NewLine, settings.ReceptacleFamilies);
        ElectricalVerticalFamiliesText = string.Join(Environment.NewLine, settings.ElectricalVerticalFamilies);
    }

    public FamilyNameSettings ToModel() => new()
    {
        WallSconceFamilies = ParseLines(WallSconceFamiliesText),
        ReceptacleFamilies = ParseLines(ReceptacleFamiliesText),
        ElectricalVerticalFamilies = ParseLines(ElectricalVerticalFamiliesText)
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
