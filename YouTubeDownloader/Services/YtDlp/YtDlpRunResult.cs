using System;

namespace YouTubeDownloader.Services;

internal sealed class YtDlpRunResult
{
    public int ExitCode { get; set; }
    public string StdErr { get; set; } = string.Empty;
    public string LastPhase { get; set; } = "(不明)";
    public TimeSpan Elapsed { get; set; }
}
