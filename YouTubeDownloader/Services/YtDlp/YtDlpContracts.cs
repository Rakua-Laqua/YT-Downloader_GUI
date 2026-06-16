using System;
using System.Threading;
using System.Threading.Tasks;
using YouTubeDownloader.Models;

namespace YouTubeDownloader.Services;

/// <summary>
/// yt-dlpとの連携を行うクライアント
/// </summary>
public interface IYtDlpClient
{
    Task<YtDlpAnalyzeResult> AnalyzeUrlAsync(string url, CancellationToken cancellationToken = default);
    Task<YtDlpUpdateResult> UpdateYtDlpAsync(string? channelOverride = null, CancellationToken cancellationToken = default);
    Task<string?> GetYtDlpVersionAsync(CancellationToken cancellationToken = default);
    Task DownloadAsync(DownloadJob job, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken = default);
}

public class YtDlpUpdateResult
{
    public bool IsSuccess { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
}

public class ProgressInfo
{
    public int Percentage { get; set; }
    public string? Status { get; set; }
    public string? Speed { get; set; }
    public string? Eta { get; set; }

    /// <summary>変換・埋め込みなどの後処理フェーズ（パーセンテージが取れない区間）かどうか</summary>
    public bool IsPostProcessing { get; set; }
}
