using System.IO;
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
    private AppSettings _settings = null!;

    public SettingsViewModel(ISettingsRepository settingsRepository)
    {
        _settingsRepository = settingsRepository;
        LoadSettings();
    }

    #region プロパティ

    [ObservableProperty]
    private string _ytDlpPath = string.Empty;

    [ObservableProperty]
    private string _ffmpegPath = string.Empty;

    [ObservableProperty]
    private string _defaultSaveFolder = string.Empty;

    [ObservableProperty]
    private string _defaultVideoFormat = "mp4";

    [ObservableProperty]
    private string _defaultAudioFormat = "mp3";

    [ObservableProperty]
    private string _defaultQuality = "best";

    [ObservableProperty]
    private string _filenameTemplate = "{title}";

    [ObservableProperty]
    private string _filenamePreview = string.Empty;

    [ObservableProperty]
    private bool _isYtDlpValid;

    [ObservableProperty]
    private bool _isFfmpegValid;

    public string[] VideoFormats { get; } = { "mp4", "mkv", "webm" };
    public string[] AudioFormats { get; } = { "mp3", "m4a", "wav" };
    public string[] Qualities { get; } = { "best", "1080p", "720p", "480p", "360p" };

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
        _settings.DefaultSaveFolder = DefaultSaveFolder;
        _settings.DefaultVideoFormat = DefaultVideoFormat;
        _settings.DefaultAudioFormat = DefaultAudioFormat;
        _settings.DefaultQuality = DefaultQuality;
        _settings.FilenameTemplate = FilenameTemplate;

        await _settingsRepository.SaveAsync(_settings);

        MessageBox.Show("設定を保存しました。", "保存完了", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    #endregion

    private void LoadSettings()
    {
        _settings = _settingsRepository.Load();

        YtDlpPath = _settings.YtDlpPath;
        FfmpegPath = _settings.FfmpegPath;
        DefaultSaveFolder = _settings.DefaultSaveFolder;
        DefaultVideoFormat = _settings.DefaultVideoFormat;
        DefaultAudioFormat = _settings.DefaultAudioFormat;
        DefaultQuality = _settings.DefaultQuality;
        FilenameTemplate = _settings.FilenameTemplate;

        ValidatePaths();
        UpdateFilenamePreview();
    }

    private void ValidatePaths()
    {
        IsYtDlpValid = !string.IsNullOrEmpty(YtDlpPath) && File.Exists(YtDlpPath);
        IsFfmpegValid = !string.IsNullOrEmpty(FfmpegPath) && File.Exists(FfmpegPath);
    }

    partial void OnFilenameTemplateChanged(string value)
    {
        UpdateFilenamePreview();
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
}
