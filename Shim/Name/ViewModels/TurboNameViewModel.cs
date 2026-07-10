#nullable disable
using System;
using System.Windows.Input;
using Autodesk.Revit.UI;
using TurboSuite.Shared.Models;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Name.ViewModels;

public class TurboNameViewModel : ViewModelBase
{
    /// <summary>CAD Room Source + region-layer configuration, edited inline in the TurboName window.</summary>
    public CadRoomSourceConfigViewModel CadConfig { get; }

    /// <summary>Set when the user clicks Run / Generate; the command reads these after the dialog closes.</summary>
    public bool ShouldRun { get; private set; }
    public bool ShouldGenerate { get; private set; }

    /// <summary>True once the user (or a pick) has changed any CAD setting — the command auto-persists the
    /// config when the window closes, but only if something actually changed.</summary>
    public bool SettingsDirty { get; private set; }

    public ICommand RunAssignCommand { get; }
    public ICommand RunGenerateCommand { get; }

    /// <summary>Set by the window code-behind; closes the window with the given DialogResult.</summary>
    public Action<bool?> CloseAction { get; set; }

    public TurboNameViewModel(CadRoomSourceSettings cadSettings, UIDocument uidoc)
    {
        CadConfig = new CadRoomSourceConfigViewModel(cadSettings, uidoc);
        CadConfig.CloseForPickRequested += () => CloseAction?.Invoke(null);
        // Any edit marks the config dirty so it's auto-saved on close.
        CadConfig.PropertyChanged += (_, __) => SettingsDirty = true;

        RunAssignCommand = new RelayCommand(() => { ShouldRun = true; CloseAction?.Invoke(true); });
        RunGenerateCommand = new RelayCommand(() => { ShouldGenerate = true; CloseAction?.Invoke(true); });
    }
}
