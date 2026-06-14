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
    private bool _isSyncingSelectedNavigation;

    public MainWindowViewModel(
        Func<DownloadViewModel> downloadViewModelFactory,
        Func<LibraryViewModel> libraryViewModelFactory,
        Func<SettingsViewModel> settingsViewModelFactory)
    {
        _downloadViewModelFactory = downloadViewModelFactory;
        _libraryViewModelFactory = libraryViewModelFactory;
        _settingsViewModelFactory = settingsViewModelFactory;

        // 初期画面はダウンロード
        _downloadViewModel = _downloadViewModelFactory();
        CurrentView = _downloadViewModel;
    }

    [ObservableProperty]
    private ViewModelBase? _currentView;

    [ObservableProperty]
    private NavigationItem _selectedNavigation = NavigationItem.Download;

    [RelayCommand]
    private void NavigateToDownload()
    {
        _ = ApplyNavigationAsync(NavigationItem.Download);
    }

    [RelayCommand]
    private async Task NavigateToLibraryAsync()
    {
        await ApplyNavigationAsync(NavigationItem.Library);
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        _ = ApplyNavigationAsync(NavigationItem.Settings);
    }

    partial void OnSelectedNavigationChanged(NavigationItem value)
    {
        if (_isSyncingSelectedNavigation)
        {
            return;
        }

        _ = ApplyNavigationAsync(value);
    }

    private async Task ApplyNavigationAsync(NavigationItem navigation)
    {
        if (SelectedNavigation != navigation)
        {
            _isSyncingSelectedNavigation = true;
            try
            {
                SelectedNavigation = navigation;
            }
            finally
            {
                _isSyncingSelectedNavigation = false;
            }
        }

        switch (navigation)
        {
            case NavigationItem.Download:
                _downloadViewModel ??= _downloadViewModelFactory();
                CurrentView = _downloadViewModel;
                break;

            case NavigationItem.Library:
                _libraryViewModel ??= _libraryViewModelFactory();
                CurrentView = _libraryViewModel;
                await _libraryViewModel.LoadAsync();
                break;

            case NavigationItem.Settings:
                _settingsViewModel ??= _settingsViewModelFactory();
                CurrentView = _settingsViewModel;
                break;
        }
    }
}
