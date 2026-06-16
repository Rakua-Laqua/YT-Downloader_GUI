using System;

namespace YouTubeDownloader.Services;

internal sealed class YtDlpDownloadInfoResult
{
    public string? FilePath { get; set; }
    public DateTime? PublishTime { get; set; }
}
