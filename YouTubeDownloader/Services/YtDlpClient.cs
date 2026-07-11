using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using YouTubeDownloader.Models;

namespace YouTubeDownloader.Services;

/// <summary>
/// yt-dlpとの連携を行うクライアント。
/// 外向きの契約を維持し、実処理は責務別クラスへ委譲する。
/// </summary>
public class YtDlpClient : IYtDlpClient
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly YtDlpAnalyzer _analyzer;
    private readonly YtDlpDownloader _downloader;
    private readonly YtDlpUpdater _updater;
    private static string? _cachedYtDlpPath;
    private static string? _cachedFfmpegPath;

    public YtDlpClient(ISettingsRepository settingsRepository, ILoggingService logger)
    {
        _settingsRepository = settingsRepository;
        _updater = new YtDlpUpdater(settingsRepository, logger, GetYtDlpPath);
        _analyzer = new YtDlpAnalyzer(settingsRepository, _updater, GetYtDlpPath, logger);
        _downloader = new YtDlpDownloader(settingsRepository, logger, _updater, GetYtDlpPath, GetFfmpegPath);
    }

    public Task<YtDlpAnalyzeResult> AnalyzeUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        return _analyzer.AnalyzeUrlAsync(url, cancellationToken);
    }

    public Task<YtDlpUpdateResult> UpdateYtDlpAsync(string? channelOverride = null, CancellationToken cancellationToken = default)
    {
        return _updater.UpdateYtDlpAsync(channelOverride, cancellationToken);
    }

    public Task<string?> GetYtDlpVersionAsync(CancellationToken cancellationToken = default)
    {
        return _updater.GetYtDlpVersionAsync(cancellationToken);
    }

    public Task DownloadAsync(DownloadJob job, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken = default)
    {
        return _downloader.DownloadAsync(job, progress, cancellationToken);
    }

    private string GetYtDlpPath()
    {
        var settings = _settingsRepository.Load();

        // 設定で指定されていればそれを使用
        if (!string.IsNullOrEmpty(settings.YtDlpPath) && File.Exists(settings.YtDlpPath))
        {
            return settings.YtDlpPath;
        }

        // キャッシュがあればそれを使用
        if (!string.IsNullOrEmpty(_cachedYtDlpPath) && File.Exists(_cachedYtDlpPath))
        {
            return _cachedYtDlpPath;
        }

        // 自動検出（検索ロジックは ExecutableLocator に集約）
        _cachedYtDlpPath = ExecutableLocator.FindExecutable("yt-dlp.exe", "yt-dlp");
        if (!string.IsNullOrEmpty(_cachedYtDlpPath))
        {
            return _cachedYtDlpPath;
        }

        throw new InvalidOperationException("yt-dlpが見つかりません。yt-dlpをインストールするか、設定画面でパスを指定してください。");
    }

    public static string? GetFfmpegPath()
    {
        // キャッシュがあればそれを使用
        if (!string.IsNullOrEmpty(_cachedFfmpegPath) && File.Exists(_cachedFfmpegPath))
        {
            return _cachedFfmpegPath;
        }

        // 自動検出
        _cachedFfmpegPath = ExecutableLocator.FindExecutable("ffmpeg.exe", "ffmpeg");
        return _cachedFfmpegPath;
    }
}
