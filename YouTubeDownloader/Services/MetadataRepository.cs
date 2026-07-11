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
    private readonly Func<string, string, Task> _writeAllTextAsync;
    // 同時ダウンロードの完了が複数ワーカースレッドから同時に保存してくるため、
    // キャッシュ(List)とファイルI/Oへのアクセスを直列化する
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private List<VideoMetadata> _cache = new();
    private IReadOnlyList<VideoMetadata> _orderedCache = Array.Empty<VideoMetadata>();
    private bool _loaded;

    public MetadataRepository(ILoggingService? logger = null)
        : this(AppStorage.GetAppFilePath("metadata.json"), logger, AtomicFileWriter.WriteAllTextAsync)
    {
    }

    internal MetadataRepository(
        string metadataFilePath,
        ILoggingService? logger,
        Func<string, string, Task> writeAllTextAsync)
    {
        _logger = logger ?? new LoggingService();
        _metadataFilePath = metadataFilePath;
        _writeAllTextAsync = writeAllTextAsync;

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
        RebuildOrderedCache();
        _loaded = true;
    }

    // _mutex 保持中に呼び出す。検索ではこの不変スナップショットをロック外で読む。
    private void RebuildOrderedCache()
    {
        _orderedCache = _cache
            .OrderByDescending(v => v.DownloadedAt)
            .ToList();
    }

    private async Task SaveAsync(IReadOnlyList<VideoMetadata> snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
        await _writeAllTextAsync(_metadataFilePath, json);
    }

    public async Task SaveVideoMetadataAsync(VideoMetadata metadata)
    {
        await _mutex.WaitAsync();
        try
        {
            await LoadIfNeededAsync();

            var updatedCache = new List<VideoMetadata>(_cache);
            var existing = updatedCache.FindIndex(v => v.Id == metadata.Id);
            if (existing >= 0)
            {
                updatedCache[existing] = metadata;
            }
            else
            {
                updatedCache.Add(metadata);
            }

            await SaveAsync(updatedCache);
            _cache = updatedCache;
            RebuildOrderedCache();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IEnumerable<VideoMetadata>> FindVideosAsync(string? searchQuery = null)
    {
        IReadOnlyList<VideoMetadata> orderedVideos;
        await _mutex.WaitAsync();
        try
        {
            await LoadIfNeededAsync();
            orderedVideos = _orderedCache;
        }
        finally
        {
            _mutex.Release();
        }

        // ライブ検索の全件走査はロック外で行う。並び替えは更新時に済ませている。
        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return orderedVideos.ToList();
        }

        return orderedVideos.Where(v =>
            v.Title.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) ||
            v.Channel.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task DeleteVideoMetadataAsync(string videoId)
    {
        await _mutex.WaitAsync();
        try
        {
            await LoadIfNeededAsync();
            var updatedCache = new List<VideoMetadata>(_cache);
            if (updatedCache.RemoveAll(v => v.Id == videoId) > 0)
            {
                await SaveAsync(updatedCache);
                _cache = updatedCache;
                RebuildOrderedCache();
            }
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
            var updatedCache = new List<VideoMetadata>(_cache);
            var removed = updatedCache.RemoveAll(v => idSet.Contains(v.Id));
            if (removed > 0)
            {
                await SaveAsync(updatedCache);
                _cache = updatedCache;
                RebuildOrderedCache();
            }
        }
        finally
        {
            _mutex.Release();
        }
    }
}
