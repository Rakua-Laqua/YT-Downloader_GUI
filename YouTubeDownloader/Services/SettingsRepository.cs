using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YouTubeDownloader.Models;

namespace YouTubeDownloader.Services;

/// <summary>
/// 設定の読み書きを行うリポジトリ
/// </summary>
public interface ISettingsRepository
{
    /// <summary>設定が保存されたときに発火する（保存後の設定を引数に渡す）</summary>
    event EventHandler<AppSettings>? SettingsSaved;

    AppSettings Load();
    Task SaveAsync(AppSettings settings);
}

public class SettingsRepository : ISettingsRepository
{
    private readonly string _settingsFilePath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILoggingService _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    // 呼び出し側が設定オブジェクトを変更してもキャッシュが書き換わらないよう、
    // 読み出し・保存・通知の境界では常にコピーを渡す。
    private AppSettings? _cachedSettings;

    public event EventHandler<AppSettings>? SettingsSaved;

    public SettingsRepository(ILoggingService? logger = null)
    {
        _logger = logger ?? new LoggingService();
        _settingsFilePath = AppStorage.GetAppFilePath("settings.json");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };
    }

    public AppSettings Load()
    {
        _mutex.Wait();
        try
        {
            if (_cachedSettings != null)
            {
                return CloneSettings(_cachedSettings);
            }

            AppSettings settings = new();
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                settings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
            }

            _cachedSettings = CloneSettings(settings);
            return settings;
        }
        catch (Exception ex)
        {
            _logger.Error("設定ファイルの読み込みに失敗しました。デフォルト設定で起動します。", ex);
            AppStorage.TryCopyUnreadableFile(_settingsFilePath, "設定", _logger);
            _cachedSettings = new AppSettings();
            return CloneSettings(_cachedSettings);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        var snapshot = CloneSettings(settings);
        var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
        await _mutex.WaitAsync();
        try
        {
            await AtomicFileWriter.WriteAllTextAsync(_settingsFilePath, json);
            _cachedSettings = CloneSettings(snapshot);
        }
        finally
        {
            _mutex.Release();
        }

        SettingsSaved?.Invoke(this, CloneSettings(snapshot));
    }

    private static AppSettings CloneSettings(AppSettings source)
    {
        return new AppSettings
        {
            YtDlpPath = source.YtDlpPath,
            FfmpegPath = source.FfmpegPath,
            CookieFilePath = source.CookieFilePath,
            AutoUpdateYtDlp = source.AutoUpdateYtDlp,
            YtDlpUpdateChannel = source.YtDlpUpdateChannel,
            DefaultMetadataLanguage = source.DefaultMetadataLanguage,
            DefaultSaveFolder = source.DefaultSaveFolder,
            DefaultVideoFormat = source.DefaultVideoFormat,
            DefaultAudioFormat = source.DefaultAudioFormat,
            DefaultQuality = source.DefaultQuality,
            DefaultAudioQuality = source.DefaultAudioQuality,
            PreferHighEfficiencyCodecs = source.PreferHighEfficiencyCodecs,
            SetFileDateToPublishDate = source.SetFileDateToPublishDate,
            FixMetadataYear = source.FixMetadataYear,
            FilenameTemplate = source.FilenameTemplate,
            MaxConcurrentDownloads = source.MaxConcurrentDownloads
        };
    }
}
