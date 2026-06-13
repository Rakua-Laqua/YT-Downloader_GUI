using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouTubeDownloader.Infrastructure;
using YouTubeDownloader.Services;

namespace YouTubeDownloader.ViewModels;

/// <summary>
/// ライブラリ画面のViewModel
/// </summary>
public partial class LibraryViewModel : ViewModelBase
{
    private readonly IMetadataRepository _metadataRepository;

    public LibraryViewModel(IMetadataRepository metadataRepository)
    {
        _metadataRepository = metadataRepository;
    }

    #region プロパティ

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private VideoMetadataViewModel? _selectedItem;

    public ObservableCollection<VideoMetadataViewModel> Items { get; } = new();

    #endregion

    #region コマンド

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        BusyMessage = "読み込み中...";

        try
        {
            Items.Clear();
            var videos = await _metadataRepository.FindVideosAsync(SearchQuery);
            foreach (var video in videos)
            {
                Items.Add(new VideoMetadataViewModel(video));
            }
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        await RefreshAsync();
    }

    [RelayCommand]
    private void OpenFolder(VideoMetadataViewModel? item)
    {
        if (item == null || string.IsNullOrEmpty(item.LocalFilePath)) return;

        var folder = Path.GetDirectoryName(item.LocalFilePath);
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{item.LocalFilePath}\"",
                UseShellExecute = true
            });
        }
    }

    [RelayCommand]
    private void PlayFile(VideoMetadataViewModel? item)
    {
        if (item == null || string.IsNullOrEmpty(item.LocalFilePath)) return;

        if (File.Exists(item.LocalFilePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = item.LocalFilePath,
                UseShellExecute = true
            });
        }
        else
        {
            MessageBox.Show("ファイルが見つかりません。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task DeleteItemAsync(VideoMetadataViewModel? item)
    {
        if (item == null) return;

        var result = MessageBox.Show(
            $"「{item.Title}」を履歴から削除しますか？\n\nファイル自体は削除されません。",
            "確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            await _metadataRepository.DeleteVideoMetadataAsync(item.Metadata.Id);
            Items.Remove(item);
        }
    }

    [RelayCommand]
    private void OpenVideoLink(VideoMetadataViewModel? item)
    {
        if (item == null)
        {
            return;
        }

        // 確認ダイアログ＋ブラウザ起動はダウンロード画面と共通
        ExternalLinkOpener.ConfirmAndOpenVideoLink(item.Url, item.Title);
    }

    #endregion

    public async Task LoadAsync()
    {
        await RefreshAsync();
    }
}
