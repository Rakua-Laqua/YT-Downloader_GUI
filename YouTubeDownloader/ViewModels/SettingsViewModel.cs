using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouTubeDownloader.Infrastructure;
using YouTubeDownloader.Models;
using YouTubeDownloader.Services;

namespace YouTubeDownloader.ViewModels;

/// <summary>
/// 設定画面のViewModel
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly IYtDlpClient _ytDlpClient;
    private AppSettings _settings = null!;
    private string? _autoDetectedYtDlpPath;
    private string? _autoDetectedFfmpegPath;

    public SettingsViewModel(ISettingsRepository settingsRepository, IYtDlpClient ytDlpClient)
    {
        _settingsRepository = settingsRepository;
        _ytDlpClient = ytDlpClient;
        LoadSettings();
    }

    #region プロパティ

    [ObservableProperty]
    private string _ytDlpPath = string.Empty;

    [ObservableProperty]
    private string _ffmpegPath = string.Empty;

    /// <summary>YouTube認証用 cookies.txt のパス（任意）</summary>
    [ObservableProperty]
    private string _cookieFilePath = string.Empty;

    /// <summary>cookieファイルの状態表示（未設定／読み込める／見つからない）</summary>
    [ObservableProperty]
    private string _cookieFileStatus = "未設定（任意）";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateYtDlpCommand))]
    private bool _autoUpdateYtDlp = true;

    [ObservableProperty]
    private string _defaultMetadataLanguage = "default";

    [ObservableProperty]
    private string _defaultSaveFolder = string.Empty;

    [ObservableProperty]
    private string _defaultVideoFormat = "mp4";

    [ObservableProperty]
    private string _defaultAudioFormat = "mp3";

    [ObservableProperty]
    private string _defaultQuality = "best";

    [ObservableProperty]
    private string _defaultAudioQuality = "標準 (VBR 5)";

    [ObservableProperty]
    private bool _preferHighEfficiencyCodecs;

    [ObservableProperty]
    private bool _setFileDateToPublishDate;

    [ObservableProperty]
    private bool _fixMetadataYear;

    [ObservableProperty]
    private string _filenameTemplate = "{title}";

    [ObservableProperty]
    private string _filenamePreview = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateYtDlpCommand))]
    private bool _isYtDlpValid;

    [ObservableProperty]
    private bool _isFfmpegValid;

    [ObservableProperty]
    private string _ytDlpAutoDetected = string.Empty;

    [ObservableProperty]
    private string _ffmpegAutoDetected = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateYtDlpCommand))]
    private bool _isUpdatingYtDlp;

    [ObservableProperty]
    private string _ytDlpUpdateStatus = string.Empty;

    /// <summary>yt-dlpの更新チャンネル（"stable" / "nightly"）</summary>
    [ObservableProperty]
    private string _ytDlpUpdateChannel = "stable";

    /// <summary>現在インストールされているyt-dlpのバージョン表示</summary>
    [ObservableProperty]
    private string _ytDlpVersion = "確認中...";

    public string[] UpdateChannels { get; } = { "stable", "nightly" };

    public string[] VideoFormats { get; } = { "mp4", "mkv", "webm" };
    public string[] AudioFormats { get; } = { "mp3", "m4a", "wav" };
    public string[] Qualities { get; } = { "best", "1080p", "720p", "480p", "360p" };
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
    public string[] MetadataLanguages { get; } = { "default", "ja", "en", "ko", "zh-Hans", "zh-Hant", "es", "fr", "de" };

    /// <summary>同時ダウンロード数</summary>
    [ObservableProperty]
    private int _maxConcurrentDownloads = 2;

    /// <summary>同時ダウンロード数の選択肢（DownloadManagerの許容範囲に合わせる）</summary>
    public int[] ConcurrencyOptions { get; } =
        Enumerable.Range(DownloadManager.MinConcurrency, DownloadManager.MaxConcurrencyLimit - DownloadManager.MinConcurrency + 1).ToArray();

    #endregion

    #region コマンド

    [RelayCommand]
    private void BrowseYtDlpPath()
    {
        var path = DialogPicker.BrowseFile(
            "yt-dlp実行ファイルを選択",
            "実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*",
            "yt-dlp.exe");
        if (path != null)
        {
            YtDlpPath = path;
            ValidatePaths();
        }
    }

    [RelayCommand]
    private void BrowseFfmpegPath()
    {
        var path = DialogPicker.BrowseFile(
            "ffmpeg実行ファイルを選択",
            "実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*",
            "ffmpeg.exe");
        if (path != null)
        {
            FfmpegPath = path;
            ValidatePaths();
        }
    }

    [RelayCommand]
    private void BrowseCookieFilePath()
    {
        var path = DialogPicker.BrowseFile(
            "cookies.txt を選択",
            "cookieファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*",
            "cookies.txt");
        if (path != null)
        {
            CookieFilePath = path;
            ValidatePaths();
        }
    }

    [RelayCommand]
    private void BrowseDefaultSaveFolder()
    {
        var path = DialogPicker.BrowseFolder("デフォルト保存先フォルダを選択", DefaultSaveFolder);
        if (path != null)
        {
            DefaultSaveFolder = path;
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        _settings.YtDlpPath = YtDlpPath;
        _settings.FfmpegPath = FfmpegPath;
        _settings.CookieFilePath = CookieFilePath?.Trim() ?? string.Empty;
        _settings.AutoUpdateYtDlp = AutoUpdateYtDlp;
        _settings.YtDlpUpdateChannel = NormalizeSelection(YtDlpUpdateChannel, UpdateChannels, "stable");
        _settings.DefaultMetadataLanguage = string.IsNullOrWhiteSpace(DefaultMetadataLanguage)
            ? "default"
            : DefaultMetadataLanguage.Trim();
        _settings.DefaultSaveFolder = DefaultSaveFolder;
        _settings.DefaultVideoFormat = DefaultVideoFormat;
        _settings.DefaultAudioFormat = DefaultAudioFormat;
        _settings.DefaultQuality = DefaultQuality;
        _settings.DefaultAudioQuality = DefaultAudioQuality;
        _settings.PreferHighEfficiencyCodecs = PreferHighEfficiencyCodecs;
        _settings.SetFileDateToPublishDate = SetFileDateToPublishDate;
        _settings.FixMetadataYear = FixMetadataYear;
        _settings.FilenameTemplate = FilenameTemplate;
        _settings.MaxConcurrentDownloads = DownloadManager.ClampConcurrency(MaxConcurrentDownloads);

        await _settingsRepository.SaveAsync(_settings);

        MessageBox.Show("設定を保存しました。", "保存完了", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand(CanExecute = nameof(CanUpdateYtDlp))]
    private async Task UpdateYtDlpAsync()
    {
        IsUpdatingYtDlp = true;
        var channel = NormalizeSelection(YtDlpUpdateChannel, UpdateChannels, "stable");
        YtDlpUpdateStatus = $"yt-dlpを更新しています...（{channel}）";

        try
        {
            var result = await _ytDlpClient.UpdateYtDlpAsync(channel);
            YtDlpUpdateStatus = result.Message;
            RefreshAutoDetectedPaths();
            await RefreshYtDlpVersionAsync();

            if (!result.IsSuccess)
            {
                MessageBox.Show(
                    string.IsNullOrWhiteSpace(result.Output) ? result.Message : result.Output,
                    "yt-dlp更新エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        catch (System.Exception ex)
        {
            YtDlpUpdateStatus = "yt-dlpの更新に失敗しました。";
            MessageBox.Show($"yt-dlpの更新に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsUpdatingYtDlp = false;
        }
    }

    #endregion

    private void LoadSettings()
    {
        _settings = _settingsRepository.Load();

        YtDlpPath = _settings.YtDlpPath;
        FfmpegPath = _settings.FfmpegPath;
        CookieFilePath = _settings.CookieFilePath;
        AutoUpdateYtDlp = _settings.AutoUpdateYtDlp;
        YtDlpUpdateChannel = NormalizeSelection(_settings.YtDlpUpdateChannel, UpdateChannels, "stable");
        DefaultMetadataLanguage = string.IsNullOrWhiteSpace(_settings.DefaultMetadataLanguage)
            ? "default"
            : _settings.DefaultMetadataLanguage;
        DefaultSaveFolder = _settings.DefaultSaveFolder;
        DefaultVideoFormat = _settings.DefaultVideoFormat;
        DefaultAudioFormat = _settings.DefaultAudioFormat;
        DefaultQuality = _settings.DefaultQuality;
        DefaultAudioQuality = NormalizeSelection(_settings.DefaultAudioQuality, AudioQualities, "標準 (VBR 5)");
        PreferHighEfficiencyCodecs = _settings.PreferHighEfficiencyCodecs;
        SetFileDateToPublishDate = _settings.SetFileDateToPublishDate;
        FixMetadataYear = _settings.FixMetadataYear;
        FilenameTemplate = _settings.FilenameTemplate;
        MaxConcurrentDownloads = DownloadManager.ClampConcurrency(_settings.MaxConcurrentDownloads);

        RefreshAutoDetectedPaths();
        YtDlpUpdateStatus = AutoUpdateYtDlp ? "初回利用前に自動更新します。" : "自動更新は無効です。";
        UpdateFilenamePreview();
        _ = RefreshYtDlpVersionAsync();
    }

    /// <summary>現在インストールされているyt-dlpのバージョンを取得して表示する</summary>
    private async Task RefreshYtDlpVersionAsync()
    {
        var version = await _ytDlpClient.GetYtDlpVersionAsync();
        YtDlpVersion = string.IsNullOrWhiteSpace(version)
            ? "未検出"
            : version!;
    }

    private void ValidatePaths()
    {
        // 手動設定のパスが有効か確認
        var hasManualYtDlp = !string.IsNullOrEmpty(YtDlpPath) && File.Exists(YtDlpPath);
        var hasManualFfmpeg = !string.IsNullOrEmpty(FfmpegPath) && File.Exists(FfmpegPath);
        var hasAutoYtDlp = !string.IsNullOrEmpty(_autoDetectedYtDlpPath) && File.Exists(_autoDetectedYtDlpPath);
        var hasAutoFfmpeg = !string.IsNullOrEmpty(_autoDetectedFfmpegPath) && File.Exists(_autoDetectedFfmpegPath);

        IsYtDlpValid = hasManualYtDlp || hasAutoYtDlp;
        IsFfmpegValid = hasManualFfmpeg || hasAutoFfmpeg;
        UpdateCookieStatus();
    }

    [RelayCommand]
    private void AutoDetectPaths()
    {
        RefreshAutoDetectedPaths();
    }

    private void RefreshAutoDetectedPaths()
    {
        // yt-dlp自動検出（YtDlpClientと同じ探索順を使い、表示と実動作のずれを防ぐ）
        var ytDlpAuto = ExecutableLocator.FindExecutable("yt-dlp.exe", "yt-dlp");
        _autoDetectedYtDlpPath = ytDlpAuto;
        if (!string.IsNullOrEmpty(ytDlpAuto))
        {
            YtDlpAutoDetected = $"自動検出: {ytDlpAuto}";
        }
        else
        {
            YtDlpAutoDetected = "自動検出: 見つかりません";
        }
        
        // ffmpeg自動検出
        var ffmpegAuto = ExecutableLocator.FindExecutable("ffmpeg.exe", "ffmpeg");
        _autoDetectedFfmpegPath = ffmpegAuto;
        if (!string.IsNullOrEmpty(ffmpegAuto))
        {
            FfmpegAutoDetected = $"自動検出: {ffmpegAuto}";
        }
        else
        {
            FfmpegAutoDetected = "自動検出: 見つかりません";
        }

        ValidatePaths();
    }

    private bool CanUpdateYtDlp()
    {
        return !IsUpdatingYtDlp && IsYtDlpValid;
    }

    partial void OnFilenameTemplateChanged(string value)
    {
        UpdateFilenamePreview();
    }

    partial void OnAutoUpdateYtDlpChanged(bool value)
    {
        YtDlpUpdateStatus = value ? "初回利用前に自動更新します。" : "自動更新は無効です。";
    }

    partial void OnCookieFilePathChanged(string value)
    {
        UpdateCookieStatus();
    }

    /// <summary>cookieファイルの設定状態をUI向けに更新する（任意設定なので未設定は警告扱いにしない）</summary>
    private void UpdateCookieStatus()
    {
        var path = CookieFilePath?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            CookieFileStatus = "未設定（任意）";
        }
        else if (File.Exists(path))
        {
            CookieFileStatus = "✓ 読み込めます";
        }
        else
        {
            CookieFileStatus = "⚠ ファイルが見つかりません";
        }
    }

    private void UpdateFilenamePreview()
    {
        var preview = FilenameTemplate
            .Replace("{title}", "サンプル動画タイトル")
            .Replace("{channel}", "チャンネル名")
            .Replace("{id}", "dQw4w9WgXcQ")
            .Replace("{index}", "01")
            .Replace("{index:02d}", "01");

        FilenamePreview = preview + ".mp4";
    }

    private static string NormalizeSelection(string? value, string[] candidates, string fallback)
    {
        return !string.IsNullOrWhiteSpace(value) && candidates.Contains(value)
            ? value
            : fallback;
    }
}
