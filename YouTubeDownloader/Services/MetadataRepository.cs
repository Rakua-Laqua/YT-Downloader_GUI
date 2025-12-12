using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using YouTubeDownloader.Models;

namespace YouTubeDownloader.Services;

/// <summary>
/// メタデータの永続化を行うリポジトリ
/// </summary>
public interface IMetadataRepository
{
    Task SaveVideoMetadataAsync(VideoMetadata metadata);
    Task<IEnumerable<VideoMetadata>> GetAllVideosAsync();
    Task<IEnumerable<VideoMetadata>> FindVideosAsync(string? searchQuery = null);
    Task DeleteVideoMetadataAsync(string videoId);
}

public class MetadataRepository : IMetadataRepository
{
    private readonly string _metadataFilePath;
    private readonly JsonSerializerOptions _jsonOptions;
    private List<VideoMetadata> _cache = new();
    private bool _loaded = false;

    public MetadataRepository()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appFolder = Path.Combine(appDataPath, "YouTubeDownloader");
        Directory.CreateDirectory(appFolder);
        _metadataFilePath = Path.Combine(appFolder, "metadata.json");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };
    }

    private async Task LoadIfNeededAsync()
    {
        if (_loaded) return;

        try
        {
            if (File.Exists(_metadataFilePath))
            {
                var json = await File.ReadAllTextAsync(_metadataFilePath);
                _cache = JsonSerializer.Deserialize<List<VideoMetadata>>(json, _jsonOptions) ?? new List<VideoMetadata>();
            }
        }
        catch
        {
            _cache = new List<VideoMetadata>();
        }
        _loaded = true;
    }

    private async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(_cache, _jsonOptions);
        await File.WriteAllTextAsync(_metadataFilePath, json);
    }

    public async Task SaveVideoMetadataAsync(VideoMetadata metadata)
    {
        await LoadIfNeededAsync();

        var existing = _cache.FindIndex(v => v.Id == metadata.Id);
        if (existing >= 0)
        {
            _cache[existing] = metadata;
        }
        else
        {
            _cache.Add(metadata);
        }

        await SaveAsync();
    }

    public async Task<IEnumerable<VideoMetadata>> GetAllVideosAsync()
    {
        await LoadIfNeededAsync();
        return _cache.OrderByDescending(v => v.DownloadedAt).ToList();
    }

    public async Task<IEnumerable<VideoMetadata>> FindVideosAsync(string? searchQuery = null)
    {
        await LoadIfNeededAsync();

        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return _cache.OrderByDescending(v => v.DownloadedAt).ToList();
        }

        var query = searchQuery.ToLowerInvariant();
        return _cache
            .Where(v =>
                v.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                v.Channel.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.DownloadedAt)
            .ToList();
    }

    public async Task DeleteVideoMetadataAsync(string videoId)
    {
        await LoadIfNeededAsync();
        _cache.RemoveAll(v => v.Id == videoId);
        await SaveAsync();
    }
}
