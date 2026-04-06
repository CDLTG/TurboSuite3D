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

    public string ProjectName { get; }
    public CutSheetsViewModel CutSheetsVM { get; }
    public ScheduleViewModel ScheduleVM { get; }
    public LoadsViewModel LoadsVM { get; }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        set => SetProperty(ref _isSettingsVisible, value);
    }

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

    public RelayCommand BrowseLogoCommand { get; }
    public RelayCommand GenerateCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }

    public DocsViewModel(List<FixtureSpecModel> cutSheetFixtures, string projectName)
    {
        ProjectName = projectName;

        // Load shared settings
        var settings = DocsSettingsService.Load();
        _logoFilePath = settings.LogoFilePath;
        _companyAddress = settings.CompanyAddress;
        _companyPhone = settings.CompanyPhone;
        _companyEmail = settings.CompanyEmail;
        _companyWebsite = settings.CompanyWebsite;
        _useLargeFormat = settings.ScheduleUseLargeFormat;

        CutSheetsVM = new CutSheetsViewModel(cutSheetFixtures, projectName, this);
        ScheduleVM = new ScheduleViewModel(projectName, this);
        LoadsVM = new LoadsViewModel(projectName, this);

        BrowseLogoCommand = new RelayCommand(ExecuteBrowseLogo);
        ToggleSettingsCommand = new RelayCommand(() => IsSettingsVisible = !IsSettingsVisible);

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
        DocsSettingsService.Save(settings);

        CutSheetsVM.SaveSettings();
        ScheduleVM.SaveSettings();
        LoadsVM.SaveSettings();
    }

    private bool CanGenerate()
    {
        if (IsSettingsVisible) return false;

        // Tab 0 = Schedule, 1 = Cut Sheets, 2 = Load Schedule
        return SelectedTabIndex switch
        {
            0 => ScheduleVM.GenerateCommand.CanExecute(null),
            1 => CutSheetsVM.GenerateCommand.CanExecute(null),
            2 => LoadsVM.GenerateCommand.CanExecute(null),
            _ => false,
        };
    }

    private void ExecuteGenerate()
    {
        switch (SelectedTabIndex)
        {
            case 0:
                ScheduleVM.GenerateCommand.Execute(null);
                break;
            case 1:
                CutSheetsVM.GenerateCommand.Execute(null);
                break;
            case 2:
                LoadsVM.GenerateCommand.Execute(null);
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
}
