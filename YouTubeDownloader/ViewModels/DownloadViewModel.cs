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
    private string _defaultVideoFormat = "mp4";
    private string _defaultAudioFormat = "mp3";
    private string _defaultVideoQuality = "best";
    private string _defaultSaveFolder = string.Empty;
    private DateTime _lastTaskbarFlashAt = DateTime.MinValue;
    private bool _hasFlashedForAllCompleted;
    private string _lastVideoQuality = "best";
    private string _lastAudioQuality = "標準 (VBR 5)";
    private bool _pendingDefaultsApply;

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
        LoadDefaultsFrom(settings);
        _saveFolderPath = _defaultSaveFolder;
        _selectedFormat = _defaultVideoFormat;
        _selectedQuality = _defaultVideoQuality;

        // 設定画面で保存されたら既定値を同期する
        _settingsRepository.SettingsSaved += OnSettingsSaved;

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

    /// <summary>取得できた件数がYouTube上の総数より少ない（取得漏れ）かどうか</summary>
    [ObservableProperty]
    private bool _isPlaylistTruncated;

    /// <summary>取得漏れ時に表示する警告文</summary>
    [ObservableProperty]
    private string _truncationWarningText = string.Empty;

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
    public string[] VideoQualities { get; } = { "best", "1080p", "720p", "480p", "360p" };
    public string[] AudioQualities { get; } =
    {
        "標準 (VBR 5)",
        "高音質 (VBR 2)",
        "最高 (VBR 0)",
        "軽量 (VBR 7)",
        "最小 (VBR 10)",
        "128K",
        "192K",
        "256K"
    };
    public string[] Qualities => VideoQualities;

    public string[] CurrentFormats => IsAudioOnly ? AudioFormats : VideoFormats;
    public string[] CurrentQualities => IsAudioOnly ? AudioQualities : VideoQualities;

    partial void OnIsAudioOnlyChanged(bool value)
    {
        // フォーマットを適切なデフォルトに変更
        SelectedFormat = value ? _defaultAudioFormat : _defaultVideoFormat;
        SelectedQuality = value ? _lastAudioQuality : _lastVideoQuality;
        OnPropertyChanged(nameof(CurrentFormats));
        OnPropertyChanged(nameof(CurrentQualities));
    }

    partial void OnSelectedQualityChanged(string value)
    {
        if (IsAudioOnly)
        {
            _lastAudioQuality = value;
        }
        else
        {
            _lastVideoQuality = value;
        }
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
        IsPlaylistTruncated = false;
        TruncationWarningText = string.Empty;
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

            // 設定が更新されていれば、この解析からフォーマット/品質の既定値を反映する
            ApplyPendingDefaultsIfNeeded();

            if (result.IsPlaylist && result.PlaylistMetadata != null)
            {
                _currentPlaylist = result.PlaylistMetadata;
                IsPlaylist = true;
                ThumbnailUrl = result.PlaylistMetadata.ThumbnailUrl;
                VideoTitle = result.PlaylistMetadata.Title;
                ChannelName = result.PlaylistMetadata.Channel;
                VideoCount = result.PlaylistMetadata.VideoCount;
                Duration = null;

                // YouTube上の総数より取得できた件数が少ない場合は警告を表示する。
                // 主な原因は古いyt-dlpのプレイリスト・ページネーション不具合（nightlyで改善する場合あり）。
                if (result.PlaylistMetadata.IsTruncated)
                {
                    IsPlaylistTruncated = true;
                    TruncationWarningText =
                        $"YouTube上の {result.PlaylistMetadata.TotalVideoCount} 本中 {result.PlaylistMetadata.VideoCount} 本のみ取得できました。" +
                        "残りは取得できていません。設定画面でyt-dlpを最新（nightly）に更新すると改善する場合があります。";
                }

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

        try
        {
            Directory.CreateDirectory(SaveFolderPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存先フォルダを作成できませんでした: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

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

    private void CancelJob(Guid jobId)
    {
        _downloadManager.Cancel(jobId);
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

    [RelayCommand]
    private void OpenVideoLink(DownloadJobViewModel? job)
    {
        if (job == null)
        {
            return;
        }

        // 確認ダイアログ＋ブラウザ起動はライブラリ画面と共通
        ExternalLinkOpener.ConfirmAndOpenVideoLink(job.Url, job.Title);
    }

    #endregion

    #region イベントハンドラ

    private void OnDownloadQueueCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 新規追加/全削除などで「全件完了」の条件が変わるので、点滅済みフラグをリセット
        _hasFlashedForAllCompleted = false;
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

            MaybeFlashWhenAllCompleted();
        });
    }

    private void MaybeFlashWhenAllCompleted()
    {
        if (DownloadQueue.Count == 0)
        {
            _hasFlashedForAllCompleted = false;
            return;
        }

        // 「全てのダウンロードが完了」= キュー内がすべて Completed
        var allCompleted = DownloadQueue.All(j => j.Status == DownloadStatus.Completed);
        if (!allCompleted)
        {
            _hasFlashedForAllCompleted = false;
            return;
        }

        if (_hasFlashedForAllCompleted)
        {
            return;
        }

        // 連続判定で過剰に点滅しないよう軽く間引く
        if ((DateTime.Now - _lastTaskbarFlashAt).TotalMilliseconds < 800)
        {
            return;
        }

        _lastTaskbarFlashAt = DateTime.Now;
        _hasFlashedForAllCompleted = true;
        TaskbarFlasher.Flash(Application.Current.MainWindow);
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

    private static string NormalizeSelection(string? value, string[] candidates, string fallback)
    {
        return !string.IsNullOrWhiteSpace(value) && candidates.Contains(value)
            ? value
            : fallback;
    }

    /// <summary>設定からフォーマット/品質の既定値を読み込む</summary>
    private void LoadDefaultsFrom(AppSettings settings)
    {
        _defaultVideoFormat = NormalizeSelection(settings.DefaultVideoFormat, VideoFormats, "mp4");
        _defaultAudioFormat = NormalizeSelection(settings.DefaultAudioFormat, AudioFormats, "mp3");
        _defaultVideoQuality = NormalizeSelection(settings.DefaultQuality, VideoQualities, "best");
        _lastVideoQuality = _defaultVideoQuality;
        _lastAudioQuality = NormalizeSelection(settings.DefaultAudioQuality, AudioQualities, "標準 (VBR 5)");
        _defaultSaveFolder = settings.DefaultSaveFolder;
    }

    private void OnSettingsSaved(object? sender, AppSettings settings)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ApplySettings(settings));
        }
        else
        {
            ApplySettings(settings);
        }
    }

    /// <summary>
    /// 設定保存時に既定値を再読込する。表示中の選択は変更せず、次回のURL解析時に新しい既定値を適用する。
    /// </summary>
    private void ApplySettings(AppSettings settings)
    {
        LoadDefaultsFrom(settings);
        _pendingDefaultsApply = true;
    }

    /// <summary>設定変更後の最初の解析時に、フォーマット/品質を新しい既定値へ揃える</summary>
    private void ApplyPendingDefaultsIfNeeded()
    {
        if (!_pendingDefaultsApply)
        {
            return;
        }

        _pendingDefaultsApply = false;
        SelectedFormat = IsAudioOnly ? _defaultAudioFormat : _defaultVideoFormat;
        SelectedQuality = IsAudioOnly ? _lastAudioQuality : _lastVideoQuality;
        SaveFolderPath = _defaultSaveFolder;
    }
}
