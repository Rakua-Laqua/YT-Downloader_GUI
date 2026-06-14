using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateYtDlpCommand))]
    private bool _autoUpdateYtDlp = true;

    [ObservableProperty]
    private string _defaultMetadataLanguage = "ja";

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
    public string[] MetadataLanguages { get; } = { "ja", "en", "default", "ko", "zh-Hans", "zh-Hant", "es", "fr", "de" };

    #endregion

    #region コマンド

    [RelayCommand]
    private void BrowseYtDlpPath()
    {
        var dialog = new OpenFileDialog
        {
            Title = "yt-dlp実行ファイルを選択",
            Filter = "実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*",
            FileName = "yt-dlp.exe"
        };

        if (dialog.ShowDialog() == true)
        {
            YtDlpPath = dialog.FileName;
            ValidatePaths();
        }
    }

    [RelayCommand]
    private void BrowseFfmpegPath()
    {
        var dialog = new OpenFileDialog
        {
            Title = "ffmpeg実行ファイルを選択",
            Filter = "実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*",
            FileName = "ffmpeg.exe"
        };

        if (dialog.ShowDialog() == true)
        {
            FfmpegPath = dialog.FileName;
            ValidatePaths();
        }
    }

    [RelayCommand]
    private void BrowseDefaultSaveFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "デフォルト保存先フォルダを選択",
            InitialDirectory = DefaultSaveFolder
        };

        if (dialog.ShowDialog() == true)
        {
            DefaultSaveFolder = dialog.FolderName;
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        _settings.YtDlpPath = YtDlpPath;
        _settings.FfmpegPath = FfmpegPath;
        _settings.AutoUpdateYtDlp = AutoUpdateYtDlp;
        _settings.DefaultMetadataLanguage = string.IsNullOrWhiteSpace(DefaultMetadataLanguage)
            ? "ja"
            : DefaultMetadataLanguage.Trim();
        _settings.DefaultSaveFolder = DefaultSaveFolder;
        _settings.DefaultVideoFormat = DefaultVideoFormat;
        _settings.DefaultAudioFormat = DefaultAudioFormat;
        _settings.DefaultQuality = DefaultQuality;
        _settings.DefaultAudioQuality = DefaultAudioQuality;
        _settings.PreferHighEfficiencyCodecs = PreferHighEfficiencyCodecs;
        _settings.FilenameTemplate = FilenameTemplate;

        await _settingsRepository.SaveAsync(_settings);

        MessageBox.Show("設定を保存しました。", "保存完了", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand(CanExecute = nameof(CanUpdateYtDlp))]
    private async Task UpdateYtDlpAsync()
    {
        IsUpdatingYtDlp = true;
        YtDlpUpdateStatus = "yt-dlpを更新しています...";

        try
        {
            var result = await _ytDlpClient.UpdateYtDlpAsync();
            YtDlpUpdateStatus = result.Message;
            ValidatePaths();

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
        AutoUpdateYtDlp = _settings.AutoUpdateYtDlp;
        DefaultMetadataLanguage = string.IsNullOrWhiteSpace(_settings.DefaultMetadataLanguage)
            ? "ja"
            : _settings.DefaultMetadataLanguage;
        DefaultSaveFolder = _settings.DefaultSaveFolder;
        DefaultVideoFormat = _settings.DefaultVideoFormat;
        DefaultAudioFormat = _settings.DefaultAudioFormat;
        DefaultQuality = _settings.DefaultQuality;
        DefaultAudioQuality = NormalizeSelection(_settings.DefaultAudioQuality, AudioQualities, "標準 (VBR 5)");
        PreferHighEfficiencyCodecs = _settings.PreferHighEfficiencyCodecs;
        FilenameTemplate = _settings.FilenameTemplate;

        ValidatePaths();
        YtDlpUpdateStatus = AutoUpdateYtDlp ? "初回利用前に自動更新します。" : "自動更新は無効です。";
        UpdateFilenamePreview();
    }

    private void ValidatePaths()
    {
        // 手動設定のパスが有効か確認
        IsYtDlpValid = !string.IsNullOrEmpty(YtDlpPath) && File.Exists(YtDlpPath);
        IsFfmpegValid = !string.IsNullOrEmpty(FfmpegPath) && File.Exists(FfmpegPath);
        
        // 自動検出されたパスを表示
        AutoDetectPaths();
    }

    [RelayCommand]
    private void AutoDetectPaths()
    {
        // yt-dlp自動検出（YtDlpClientと同じ探索順を使い、表示と実動作のずれを防ぐ）
        var ytDlpAuto = ExecutableLocator.FindExecutable("yt-dlp.exe", "yt-dlp");
        if (!string.IsNullOrEmpty(ytDlpAuto))
        {
            YtDlpAutoDetected = $"自動検出: {ytDlpAuto}";
            if (!IsYtDlpValid)
            {
                IsYtDlpValid = true; // 自動検出でも有効とする
            }
        }
        else
        {
            YtDlpAutoDetected = "自動検出: 見つかりません";
        }
        
        // ffmpeg自動検出
        var ffmpegAuto = ExecutableLocator.FindExecutable("ffmpeg.exe", "ffmpeg");
        if (!string.IsNullOrEmpty(ffmpegAuto))
        {
            FfmpegAutoDetected = $"自動検出: {ffmpegAuto}";
            if (!IsFfmpegValid)
            {
                IsFfmpegValid = true; // 自動検出でも有効とする
            }
        }
        else
        {
            FfmpegAutoDetected = "自動検出: 見つかりません";
        }
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
