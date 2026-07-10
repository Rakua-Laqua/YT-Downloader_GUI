using System;

namespace YouTubeDownloader.Services;

public sealed class YtDlpDownloadException : Exception
{
    public YtDlpDownloadException(
        string message,
        string? failureDetail = null,
        bool retriedWithFallbackClient = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureDetail = failureDetail;
        RetriedWithFallbackClient = retriedWithFallbackClient;
    }

    public string? FailureDetail { get; }
    public bool RetriedWithFallbackClient { get; }
}
