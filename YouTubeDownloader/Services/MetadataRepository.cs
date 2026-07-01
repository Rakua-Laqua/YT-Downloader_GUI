using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YouTubeDownloader.Models;

namespace YouTubeDownloader.Services;

/// <summary>
/// メタデータの永続化を行うリポジトリ
/// </summary>
public interface IMetadataRepository
{
    Task SaveVideoMetadataAsync(VideoMetadata metadata);
    Task<IEnumerable<VideoMetadata>> FindVideosAsync(string? searchQuery = null);
    Task DeleteVideoMetadataAsync(string videoId);
    Task DeleteVideoMetadataAsync(IEnumerable<string> videoIds);
}

public class MetadataRepository : IMetadataRepository
{
    private readonly string _metadataFilePath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILoggingService _logger;
    // 同時ダウンロードの完了が複数ワーカースレッドから同時に保存してくるため、
    // キャッシュ(List)とファイルI/Oへのアクセスを直列化する
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private List<VideoMetadata> _cache = new();
    private bool _loaded;

    public MetadataRepository(ILoggingService? logger = null)
    {
        _logger = logger ?? new LoggingService();
        _metadataFilePath = AppStorage.GetAppFilePath("metadata.json");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };
    }

    // 以下のprivateメソッドは必ず _mutex 保持中に呼ぶこと
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
        catch (Exception ex)
        {
            _logger.Error("ライブラリ履歴ファイルの読み込みに失敗しました。空の履歴として扱います。", ex);
            AppStorage.TryCopyUnreadableFile(_metadataFilePath, "ライブラリ履歴", _logger);
            _cache = new List<VideoMetadata>();
        }
        _loaded = true;
    }

    private async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(_cache, _jsonOptions);
        await AtomicFileWriter.WriteAllTextAsync(_metadataFilePath, json);
    }

    public async Task SaveVideoMetadataAsync(VideoMetadata metadata)
    {
        await _mutex.WaitAsync();
        try
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
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IEnumerable<VideoMetadata>> FindVideosAsync(string? searchQuery = null)
    {
        await _mutex.WaitAsync();
        try
        {
            await LoadIfNeededAsync();

            IEnumerable<VideoMetadata> videos = _cache;
            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                videos = videos.Where(v =>
                    v.Title.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) ||
                    v.Channel.Contains(searchQuery, StringComparison.OrdinalIgnoreCase));
            }

            return videos.OrderByDescending(v => v.DownloadedAt).ToList();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task DeleteVideoMetadataAsync(string videoId)
    {
        await _mutex.WaitAsync();
        try
        {
            await LoadIfNeededAsync();
            _cache.RemoveAll(v => v.Id == videoId);
            await SaveAsync();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task DeleteVideoMetadataAsync(IEnumerable<string> videoIds)
    {
        // 一括削除はまとめて1回だけ保存する（件数分のファイル書き込みを避ける）
        var idSet = videoIds as ISet<string> ?? new HashSet<string>(videoIds);
        if (idSet.Count == 0)
        {
            return;
        }

        await _mutex.WaitAsync();
        try
        {
            await LoadIfNeededAsync();
            var removed = _cache.RemoveAll(v => idSet.Contains(v.Id));
            if (removed > 0)
            {
                await SaveAsync();
            }
        }
        finally
        {
            _mutex.Release();
        }
    }
}
