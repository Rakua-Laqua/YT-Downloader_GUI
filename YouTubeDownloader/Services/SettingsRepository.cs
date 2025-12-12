using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using YouTubeDownloader.Models;

namespace YouTubeDownloader.Services;

/// <summary>
/// 設定の読み書きを行うリポジトリ
/// </summary>
public interface ISettingsRepository
{
    AppSettings Load();
    Task SaveAsync(AppSettings settings);
}

public class SettingsRepository : ISettingsRepository
{
    private readonly string _settingsFilePath;
    private readonly JsonSerializerOptions _jsonOptions;

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
        return new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, _jsonOptions);
        await File.WriteAllTextAsync(_settingsFilePath, json);
    }
}
