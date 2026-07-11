using YouTubeDownloader.Services;

namespace YouTubeDownloader.Tests;

internal sealed class TestLogger : ILoggingService
{
    public string LogDirectory => string.Empty;
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? exception = null) { }
}
