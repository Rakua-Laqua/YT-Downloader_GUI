using YouTubeDownloader.Models;
using YouTubeDownloader.Services;

namespace YouTubeDownloader.Tests;

public sealed class DownloadManagerTests
{
    [Fact]
    public async Task MetadataSaveFailure_CompletesWithWarningWithoutExposingExceptionMessage()
    {
        using var manager = new DownloadManager(
            new SuccessfulYtDlpClient(),
            new FailingMetadataRepository(),
            new TestSettingsRepository(),
            new TestLogger());
        var terminalStatus = new TaskCompletionSource<DownloadJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.JobStatusChanged += (_, args) =>
        {
            if (args.Job.Status == DownloadStatus.CompletedWithWarning)
            {
                terminalStatus.TrySetResult(args.Job);
            }
        };

        manager.Enqueue(new DownloadJob
        {
            VideoMetadata = new VideoMetadata { Id = "video-1", Title = "test", Url = "https://example.test/video" }
        });

        var job = await terminalStatus.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadStatus.CompletedWithWarning, job.Status);
        Assert.DoesNotContain("secret failure detail", job.ErrorMessage);
    }

    private sealed class SuccessfulYtDlpClient : IYtDlpClient
    {
        public Task<YtDlpAnalyzeResult> AnalyzeUrlAsync(string url, CancellationToken cancellationToken = default) =>
            Task.FromResult(new YtDlpAnalyzeResult());
        public Task<YtDlpUpdateResult> UpdateYtDlpAsync(string? channelOverride = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new YtDlpUpdateResult());
        public Task<string?> GetYtDlpVersionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
        public Task DownloadAsync(DownloadJob job, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FailingMetadataRepository : IMetadataRepository
    {
        public Task SaveVideoMetadataAsync(VideoMetadata metadata) =>
            Task.FromException(new IOException("secret failure detail"));
        public Task<IEnumerable<VideoMetadata>> FindVideosAsync(string? searchQuery = null) =>
            Task.FromResult(Enumerable.Empty<VideoMetadata>());
        public Task DeleteVideoMetadataAsync(string videoId) => Task.CompletedTask;
        public Task DeleteVideoMetadataAsync(IEnumerable<string> videoIds) => Task.CompletedTask;
    }

    private sealed class TestSettingsRepository : ISettingsRepository
    {
        public event EventHandler<AppSettings>? SettingsSaved;
        public AppSettings Load() => new() { MaxConcurrentDownloads = 1 };
        public Task SaveAsync(AppSettings settings)
        {
            SettingsSaved?.Invoke(this, settings);
            return Task.CompletedTask;
        }
    }
}
