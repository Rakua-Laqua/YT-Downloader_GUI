using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using YouTubeDownloader.Infrastructure;
using YouTubeDownloader.Models;
using YouTubeDownloader.Services;

namespace YouTubeDownloader.ViewModels;

/// <summary>
/// ダウンロード画面のViewModel
/// </summary>
public partial class DownloadViewModel : ViewModelBase
{
    private readonly IYtDlpClient _ytDlpClient;
    private readonly IDownloadManager _downloadManager;
    private readonly ISettingsRepository _settingsRepository;
    private DateTime _lastTaskbarFlashAt = DateTime.MinValue;

    public DownloadViewModel(
        IYtDlpClient ytDlpClient,
        IDownloadManager downloadManager,
        ISettingsRepository settingsRepository)
    {
        _ytDlpClient = ytDlpClient;
        _downloadManager = downloadManager;
        _settingsRepository = settingsRepository;

        _downloadManager.JobProgressChanged += OnJobProgressChanged;
        _downloadManager.JobStatusChanged += OnJobStatusChanged;

        // 設定を読み込み
        var settings = _settingsRepository.Load();
        _saveFolderPath = settings.DefaultSaveFolder;
        _selectedFormat = settings.DefaultVideoFormat;
        _selectedQuality = settings.DefaultQuality;

        // ダウンロードキューを復元
        foreach (var job in _downloadManager.GetAllJobs())
        {
            var vm = new DownloadJobViewModel(job, CancelJob, RetryJob);
            vm.UpdateFromJob();
            DownloadQueue.Add(vm);
        }

        DownloadQueue.CollectionChanged += OnDownloadQueueCollectionChanged;
        UpdateQueueSummary();
    }

    #region プロパティ

    [ObservableProperty]
    private string _inputUrl = string.Empty;

    [ObservableProperty]
    private string _saveFolderPath = string.Empty;

    [ObservableProperty]
    private string _selectedFormat = "mp4";

    [ObservableProperty]
    private string _selectedQuality = "best";

    [ObservableProperty]
    private bool _isAnalyzing;

    [ObservableProperty]
    private bool _hasAnalyzedResult;

    [ObservableProperty]
    private bool _isPlaylist;

    [ObservableProperty]
    private string? _thumbnailUrl;

    [ObservableProperty]
    private string? _videoTitle;

    [ObservableProperty]
    private string? _channelName;

    [ObservableProperty]
    private int _videoCount;

    [ObservableProperty]
    private string? _duration;

    [ObservableProperty]
    private bool _isAudioOnly;

    private VideoMetadata? _currentVideo;
    private PlaylistMetadata? _currentPlaylist;

    public ObservableCollection<DownloadJobViewModel> DownloadQueue { get; } = new();
    public ObservableCollection<PlaylistItemViewModel> PlaylistItems { get; } = new();

    public int DownloadQueueTotal => DownloadQueue.Count;
    public int DownloadQueueCompleted => DownloadQueue.Count(j => j.Status == DownloadStatus.Completed);
    public string DownloadQueueSummaryText => $"{DownloadQueueCompleted}/{DownloadQueueTotal}";

    public string[] VideoFormats { get; } = { "mp4", "mkv", "webm" };
    public string[] AudioFormats { get; } = { "mp3", "m4a", "wav" };
    public string[] Qualities { get; } = { "best", "1080p", "720p", "480p" };

    public string[] CurrentFormats => IsAudioOnly ? AudioFormats : VideoFormats;

    partial void OnIsAudioOnlyChanged(bool value)
    {
        // フォーマットを適切なデフォルトに変更
        SelectedFormat = value ? "mp3" : "mp4";
        OnPropertyChanged(nameof(CurrentFormats));
    }

    #endregion

    #region コマンド

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (string.IsNullOrWhiteSpace(InputUrl))
        {
            MessageBox.Show("URLを入力してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsAnalyzing = true;
        HasAnalyzedResult = false;
        _currentVideo = null;
        _currentPlaylist = null;
        PlaylistItems.Clear();

        try
        {
            var result = await _ytDlpClient.AnalyzeUrlAsync(InputUrl);

            if (!result.IsSuccess)
            {
                MessageBox.Show(result.ErrorMessage ?? "解析に失敗しました。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (result.IsPlaylist && result.PlaylistMetadata != null)
            {
                _currentPlaylist = result.PlaylistMetadata;
                IsPlaylist = true;
                ThumbnailUrl = result.PlaylistMetadata.ThumbnailUrl;
                VideoTitle = result.PlaylistMetadata.Title;
                ChannelName = result.PlaylistMetadata.Channel;
                VideoCount = result.PlaylistMetadata.VideoCount;
                Duration = null;

                foreach (var video in result.PlaylistMetadata.Videos)
                {
                    PlaylistItems.Add(new PlaylistItemViewModel(video));
                }
            }
            else if (result.VideoMetadata != null)
            {
                _currentVideo = result.VideoMetadata;
                IsPlaylist = false;
                ThumbnailUrl = result.VideoMetadata.ThumbnailUrl;
                VideoTitle = result.VideoMetadata.Title;
                ChannelName = result.VideoMetadata.Channel;
                Duration = result.VideoMetadata.DurationFormatted;
                VideoCount = 1;
            }

            HasAnalyzedResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"解析エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    [RelayCommand]
    private void BrowseSaveFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "保存先フォルダを選択",
            InitialDirectory = SaveFolderPath
        };

        if (dialog.ShowDialog() == true)
        {
            SaveFolderPath = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void StartDownload()
    {
        if (!HasAnalyzedResult)
        {
            MessageBox.Show("まずURLを解析してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(SaveFolderPath))
        {
            MessageBox.Show("保存先フォルダを指定してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Directory.CreateDirectory(SaveFolderPath);

        if (IsPlaylist && _currentPlaylist != null)
        {
            // プレイリストの場合
            var selectedItems = PlaylistItems.Where(i => i.IsSelected).ToList();
            if (!selectedItems.Any())
            {
                MessageBox.Show("ダウンロードする動画を選択してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // プレイリスト名のフォルダを作成
            var playlistFolder = Path.Combine(SaveFolderPath, SanitizeFolderName(_currentPlaylist.Title));

            foreach (var item in selectedItems)
            {
                var job = new DownloadJob
                {
                    TargetType = DownloadTargetType.PlaylistItem,
                    VideoMetadata = item.Video,
                    SaveFolderPath = playlistFolder,
                    Format = SelectedFormat,
                    Quality = SelectedQuality
                };

                var jobVm = new DownloadJobViewModel(job, CancelJob, RetryJob);
                DownloadQueue.Add(jobVm);
                _downloadManager.Enqueue(job);
            }
        }
        else if (_currentVideo != null)
        {
            // 単一動画の場合
            var job = new DownloadJob
            {
                TargetType = DownloadTargetType.SingleVideo,
                VideoMetadata = _currentVideo,
                SaveFolderPath = SaveFolderPath,
                Format = SelectedFormat,
                Quality = SelectedQuality
            };

            var jobVm = new DownloadJobViewModel(job, CancelJob, RetryJob);
            DownloadQueue.Add(jobVm);
            _downloadManager.Enqueue(job);
        }

        // 解析結果をリセット
        HasAnalyzedResult = false;
        InputUrl = string.Empty;
        PlaylistItems.Clear();
    }

    [RelayCommand]
    private void SelectAllPlaylistItems()
    {
        foreach (var item in PlaylistItems)
        {
            item.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearAllPlaylistItems()
    {
        foreach (var item in PlaylistItems)
        {
            item.IsSelected = false;
        }
    }

    [RelayCommand]
    private void CancelJob(DownloadJobViewModel? job)
    {
        if (job != null)
        {
            _downloadManager.Cancel(job.Job.Id);
        }
    }

    private void CancelJob(Guid jobId)
    {
        _downloadManager.Cancel(jobId);
    }

    [RelayCommand]
    private void RetryJob(DownloadJobViewModel? job)
    {
        if (job != null)
        {
            _downloadManager.Retry(job.Job.Id);
        }
    }

    private void RetryJob(Guid jobId)
    {
        _downloadManager.Retry(jobId);
    }

    [RelayCommand]
    private void CancelAllJobs()
    {
        _downloadManager.CancelAll();
    }

    [RelayCommand]
    private void ClearAllJobs()
    {
        var result = MessageBox.Show(
            "ダウンロードキューをすべて削除します。実行中のダウンロードはキャンセルされます。\nよろしいですか？",
            "確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        DownloadQueue.Clear();
        _downloadManager.ClearAll();
    }

    [RelayCommand]
    private void ClearCompletedJobs()
    {
        var completedJobs = DownloadQueue
            .Where(j => j.Status == DownloadStatus.Completed || j.Status == DownloadStatus.Canceled)
            .ToList();

        foreach (var job in completedJobs)
        {
            DownloadQueue.Remove(job);
        }

        _downloadManager.ClearCompleted();
    }

    #endregion

    #region イベントハンドラ

    private void OnDownloadQueueCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateQueueSummary();
    }

    private void UpdateQueueSummary()
    {
        OnPropertyChanged(nameof(DownloadQueueTotal));
        OnPropertyChanged(nameof(DownloadQueueCompleted));
        OnPropertyChanged(nameof(DownloadQueueSummaryText));
    }

    private void OnJobProgressChanged(object? sender, DownloadJobEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var jobVm = DownloadQueue.FirstOrDefault(j => j.Job.Id == e.Job.Id);
            jobVm?.UpdateFromJob();
        });
    }

    private void OnJobStatusChanged(object? sender, DownloadJobEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var jobVm = DownloadQueue.FirstOrDefault(j => j.Job.Id == e.Job.Id);
            jobVm?.UpdateFromJob();
            UpdateQueueSummary();

            if (e.Job.Status == DownloadStatus.Completed)
            {
                // 連続完了で過剰に点滅しないよう軽く間引く
                if ((DateTime.Now - _lastTaskbarFlashAt).TotalMilliseconds >= 800)
                {
                    _lastTaskbarFlashAt = DateTime.Now;
                    TaskbarFlasher.Flash(Application.Current.MainWindow);
                }
            }
        });
    }

    #endregion

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidPathChars().Concat(Path.GetInvalidFileNameChars()).Distinct();
        foreach (var c in invalid)
        {
            name = name.Replace(c, '_');
        }
        return name;
    }
}
