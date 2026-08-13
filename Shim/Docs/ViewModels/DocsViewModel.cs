using System;
using System.Collections.Generic;
using Microsoft.Win32;
using TurboSuite.Docs.Models;
using TurboSuite.Docs.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Docs.ViewModels;

public class DocsViewModel : ViewModelBase
{
    private int _selectedTabIndex;
    private bool _isSettingsVisible;
    private string _statusText = string.Empty;
    private bool _isError;
    private string _logoFilePath = string.Empty;
    private string _companyAddress = string.Empty;
    private string _companyPhone = string.Empty;
    private string _companyEmail = string.Empty;
    private string _companyWebsite = string.Empty;
    private DateTime _headerDate = DateTime.Now;
    private bool _useLargeFormat;
    private string _coverBrandingVerticalPath = string.Empty;
    private string _coverBrandingHorizontalPath = string.Empty;
    private string _projectLocation = string.Empty;

    public string ProjectName { get; }
    public string ProjectNumber { get; }
    public CutSheetsViewModel CutSheetsVM { get; }
    public ScheduleViewModel ScheduleVM { get; }
    public PowerSuppliesViewModel PowerSuppliesVM { get; }
    public LoadsViewModel LoadsVM { get; }
    public PanelScheduleViewModel PanelScheduleVM { get; }
    public NotesViewModel NotesVM { get; }
    public BomViewModel BomVM { get; }
    public CountsViewModel CountsVM { get; }

    // Tab index of the Counts tab in TurboDocsWindow.xaml — gates the Counts-only footer
    // controls (the Legacy Counts button lives beside Generate, but only there).
    private const int CountsTabIndex = 7;

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetProperty(ref _selectedTabIndex, value))
                OnPropertyChanged(nameof(IsCountsTabActive));
        }
    }

    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        set
        {
            if (SetProperty(ref _isSettingsVisible, value))
                OnPropertyChanged(nameof(IsCountsTabActive));
        }
    }

    // The Counts tab is the front tab (settings panel hidden). Shows the Legacy Counts footer button.
    public bool IsCountsTabActive => SelectedTabIndex == CountsTabIndex && !IsSettingsVisible;

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (SetProperty(ref _statusText, value))
                IsError = value.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool IsError
    {
        get => _isError;
        set => SetProperty(ref _isError, value);
    }

    public string LogoFilePath
    {
        get => _logoFilePath;
        set => SetProperty(ref _logoFilePath, value);
    }

    public string CompanyAddress
    {
        get => _companyAddress;
        set => SetProperty(ref _companyAddress, value);
    }

    public string CompanyPhone
    {
        get => _companyPhone;
        set => SetProperty(ref _companyPhone, value);
    }

    public string CompanyEmail
    {
        get => _companyEmail;
        set => SetProperty(ref _companyEmail, value);
    }

    public string CompanyWebsite
    {
        get => _companyWebsite;
        set => SetProperty(ref _companyWebsite, value);
    }

    public DateTime HeaderDate
    {
        get => _headerDate;
        set => SetProperty(ref _headerDate, value);
    }

    public bool UseLargeFormat
    {
        get => _useLargeFormat;
        set => SetProperty(ref _useLargeFormat, value);
    }

    public string CoverBrandingVerticalPath
    {
        get => _coverBrandingVerticalPath;
        set => SetProperty(ref _coverBrandingVerticalPath, value);
    }

    public string CoverBrandingHorizontalPath
    {
        get => _coverBrandingHorizontalPath;
        set => SetProperty(ref _coverBrandingHorizontalPath, value);
    }

    public string ProjectLocation
    {
        get => _projectLocation;
        set => SetProperty(ref _projectLocation, value);
    }

    public RelayCommand BrowseLogoCommand { get; }
    public RelayCommand BrowseCoverVerticalCommand { get; }
    public RelayCommand BrowseCoverHorizontalCommand { get; }
    public RelayCommand GenerateCommand { get; }
    public RelayCommand LegacyCountsCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }

    public DocsViewModel(List<FixtureSpecModel> cutSheetFixtures, List<FixtureSpecModel> rpsCutSheetFixtures, string projectName, string projectNumber = "")
    {
        ProjectName = projectName;
        ProjectNumber = projectNumber;

        // Load shared settings
        var settings = DocsSettingsService.Load();
        _logoFilePath = settings.LogoFilePath;
        _companyAddress = settings.CompanyAddress;
        _companyPhone = settings.CompanyPhone;
        _companyEmail = settings.CompanyEmail;
        _companyWebsite = settings.CompanyWebsite;
        _useLargeFormat = settings.ScheduleUseLargeFormat;
        _coverBrandingVerticalPath = settings.CoverBrandingVerticalPath;
        _coverBrandingHorizontalPath = settings.CoverBrandingHorizontalPath;
        _projectLocation = settings.ProjectLocation;
        _selectedTabIndex = settings.SelectedTabIndex;

        // Combine fixture + RPS cut sheets (fixtures first, then RPS)
        var allCutSheets = new List<FixtureSpecModel>(cutSheetFixtures);
        allCutSheets.AddRange(rpsCutSheetFixtures);

        CutSheetsVM = new CutSheetsViewModel(allCutSheets, projectName, this);
        ScheduleVM = new ScheduleViewModel(projectName, this);
        PowerSuppliesVM = new PowerSuppliesViewModel(projectName, this);
        LoadsVM = new LoadsViewModel(projectName, this);
        PanelScheduleVM = new PanelScheduleViewModel(projectName, this);
        NotesVM = new NotesViewModel(projectName, this);
        BomVM = new BomViewModel(projectName, this);
        CountsVM = new CountsViewModel(projectName, projectNumber, this);

        BrowseLogoCommand = new RelayCommand(ExecuteBrowseLogo);
        BrowseCoverVerticalCommand = new RelayCommand(ExecuteBrowseCoverVertical);
        BrowseCoverHorizontalCommand = new RelayCommand(ExecuteBrowseCoverHorizontal);
        ToggleSettingsCommand = new RelayCommand(() => IsSettingsVisible = !IsSettingsVisible);
        LegacyCountsCommand = new RelayCommand(
            () => CountsVM.LegacyCountsCommand.Execute(null),
            () => CountsVM.LegacyCountsCommand.CanExecute(null));

        // Forward status text from active tab VM
        CutSheetsVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CutSheetsViewModel.StatusText))
                StatusText = CutSheetsVM.StatusText;
        };
        ScheduleVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScheduleViewModel.StatusText))
                StatusText = ScheduleVM.StatusText;
        };
        LoadsVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LoadsViewModel.StatusText))
                StatusText = LoadsVM.StatusText;
        };
        PanelScheduleVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PanelScheduleViewModel.StatusText))
                StatusText = PanelScheduleVM.StatusText;
        };
        NotesVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NotesViewModel.StatusText))
                StatusText = NotesVM.StatusText;
        };
        BomVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BomViewModel.StatusText))
                StatusText = BomVM.StatusText;
        };
        PowerSuppliesVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PowerSuppliesViewModel.StatusText))
                StatusText = PowerSuppliesVM.StatusText;
        };
        CountsVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CountsViewModel.StatusText))
                StatusText = CountsVM.StatusText;
        };

        GenerateCommand = new RelayCommand(ExecuteGenerate, CanGenerate);

        // Re-evaluate CanGenerate when tab changes or generating state changes
        CutSheetsVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CutSheetsViewModel.IsGenerating))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        };
        ScheduleVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ScheduleViewModel.IsGenerating))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        };
        LoadsVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LoadsViewModel.IsGenerating))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        };
        PanelScheduleVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PanelScheduleViewModel.IsGenerating))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        };
        NotesVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(NotesViewModel.IsGenerating))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        };
        BomVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BomViewModel.IsGenerating))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        };
        PowerSuppliesVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PowerSuppliesViewModel.IsGenerating))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        };
        CountsVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CountsViewModel.IsGenerating))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        };
    }

    public void SaveSettings()
    {
        var settings = DocsSettingsService.Load();
        settings.LogoFilePath = LogoFilePath;
        settings.CompanyAddress = CompanyAddress;
        settings.CompanyPhone = CompanyPhone;
        settings.CompanyEmail = CompanyEmail;
        settings.CompanyWebsite = CompanyWebsite;
        settings.ScheduleUseLargeFormat = UseLargeFormat;
        settings.SelectedTabIndex = SelectedTabIndex;
        settings.CoverBrandingVerticalPath = CoverBrandingVerticalPath;
        settings.CoverBrandingHorizontalPath = CoverBrandingHorizontalPath;
        settings.ProjectLocation = ProjectLocation;
        DocsSettingsService.Save(settings);

        CutSheetsVM.SaveSettings();
        ScheduleVM.SaveSettings();
        PowerSuppliesVM.SaveSettings();
        LoadsVM.SaveSettings();
        PanelScheduleVM.SaveSettings();
        NotesVM.SaveSettings();
        BomVM.SaveSettings();
        CountsVM.SaveSettings();
    }

    private bool CanGenerate()
    {
        if (IsSettingsVisible) return false;

        // Tab 0 = Cover, 1 = Fixture Schedule, 2 = Power Supplies, 3 = Cut Sheets, 4 = Control BOM, 5 = Load Schedule, 6 = Panel Schedule, 7 = Counts
        return SelectedTabIndex switch
        {
            0 => NotesVM.GenerateCommand.CanExecute(null),
            1 => ScheduleVM.GenerateCommand.CanExecute(null),
            2 => PowerSuppliesVM.GenerateCommand.CanExecute(null),
            3 => CutSheetsVM.GenerateCommand.CanExecute(null),
            4 => BomVM.GenerateCommand.CanExecute(null),
            5 => LoadsVM.GenerateCommand.CanExecute(null),
            6 => PanelScheduleVM.GenerateCommand.CanExecute(null),
            7 => CountsVM.GenerateCommand.CanExecute(null),
            _ => false,
        };
    }

    private void ExecuteGenerate()
    {
        switch (SelectedTabIndex)
        {
            case 0:
                NotesVM.GenerateCommand.Execute(null);
                break;
            case 1:
                ScheduleVM.GenerateCommand.Execute(null);
                break;
            case 2:
                PowerSuppliesVM.GenerateCommand.Execute(null);
                break;
            case 3:
                CutSheetsVM.GenerateCommand.Execute(null);
                break;
            case 4:
                BomVM.GenerateCommand.Execute(null);
                break;
            case 5:
                LoadsVM.GenerateCommand.Execute(null);
                break;
            case 6:
                PanelScheduleVM.GenerateCommand.Execute(null);
                break;
            case 7:
                CountsVM.GenerateCommand.Execute(null);
                break;
        }
    }

    private void ExecuteBrowseLogo()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.pdf",
            Title = "Select Company Logo"
        };
        if (dialog.ShowDialog() == true)
            LogoFilePath = dialog.FileName;
    }

    private void ExecuteBrowseCoverVertical()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.pdf",
            Title = "Select Cover Page Vertical Branding Image"
        };
        if (dialog.ShowDialog() == true)
            CoverBrandingVerticalPath = dialog.FileName;
    }

    private void ExecuteBrowseCoverHorizontal()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.pdf",
            Title = "Select Cover Page Horizontal Branding Image"
        };
        if (dialog.ShowDialog() == true)
            CoverBrandingHorizontalPath = dialog.FileName;
    }
}
