using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace YouTubeDownloader.ViewModels;

/// <summary>
/// ナビゲーションアイテムの種類
/// </summary>
public enum NavigationItem
{
    Download,
    Library,
    Settings
}

/// <summary>
/// メインウィンドウのViewModel
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly Func<DownloadViewModel> _downloadViewModelFactory;
    private readonly Func<LibraryViewModel> _libraryViewModelFactory;
    private readonly Func<SettingsViewModel> _settingsViewModelFactory;

    private DownloadViewModel? _downloadViewModel;
    private LibraryViewModel? _libraryViewModel;
    private SettingsViewModel? _settingsViewModel;

    public MainWindowViewModel(
        Func<DownloadViewModel> downloadViewModelFactory,
        Func<LibraryViewModel> libraryViewModelFactory,
        Func<SettingsViewModel> settingsViewModelFactory)
    {
        _downloadViewModelFactory = downloadViewModelFactory;
        _libraryViewModelFactory = libraryViewModelFactory;
        _settingsViewModelFactory = settingsViewModelFactory;

        // 初期画面はダウンロード
        NavigateToDownload();
    }

    [ObservableProperty]
    private ViewModelBase? _currentView;

    [ObservableProperty]
    private NavigationItem _selectedNavigation = NavigationItem.Download;

    [RelayCommand]
    private void NavigateToDownload()
    {
        _downloadViewModel ??= _downloadViewModelFactory();
        CurrentView = _downloadViewModel;
        SelectedNavigation = NavigationItem.Download;
    }

    [RelayCommand]
    private async Task NavigateToLibraryAsync()
    {
        _libraryViewModel ??= _libraryViewModelFactory();
        CurrentView = _libraryViewModel;
        SelectedNavigation = NavigationItem.Library;
        await _libraryViewModel.LoadAsync();
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        _settingsViewModel ??= _settingsViewModelFactory();
        CurrentView = _settingsViewModel;
        SelectedNavigation = NavigationItem.Settings;
    }
}
