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
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public event EventHandler<AppSettings>? SettingsSaved;

    public SettingsRepository()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appFolder = Path.Combine(appDataPath, "YouTubeDownloader");
        Directory.CreateDirectory(appFolder);
        _settingsFilePath = Path.Combine(appFolder, "settings.json");

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
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                return JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            // 読み込み失敗時はデフォルト設定を返す
        }
        finally
        {
            _mutex.Release();
        }

        return new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, _jsonOptions);
        await _mutex.WaitAsync();
        try
        {
            await AtomicFileWriter.WriteAllTextAsync(_settingsFilePath, json);
        }
        finally
        {
            _mutex.Release();
        }

        SettingsSaved?.Invoke(this, settings);
    }
}
