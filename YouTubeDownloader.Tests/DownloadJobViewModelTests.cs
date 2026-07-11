using YouTubeDownloader.Models;
using YouTubeDownloader.ViewModels;

namespace YouTubeDownloader.Tests;

public sealed class DownloadJobViewModelTests
{
    [Fact]
    public void CompletionTooltip_NormalizesEquivalentCodecTags()
    {
        var job = new DownloadJob
        {
            Status = DownloadStatus.Completed,
            StartedAt = DateTime.Now.AddSeconds(-1),
            CompletedAt = DateTime.Now,
            VideoMetadata = new VideoMetadata { DurationSeconds = 60 },
            SourceExt = "mp4",
            SourceVcodec = "av01.0.08M.08",
            SourceAcodec = "mp4a.40.2",
            ActualExt = "mp4",
            ActualVcodec = "av1",
            ActualAcodec = "aac"
        };
        var viewModel = new DownloadJobViewModel(job);
        viewModel.UpdateFromJob();

        Assert.Contains("yt-dlp選択: MP4 / AV1 / AAC-LC", viewModel.CompletionInfoTooltip);
        Assert.Contains("ffprobe実ファイル: MP4 / AV1 / AAC-LC", viewModel.CompletionInfoTooltip);
        Assert.Contains("詳細タグ: yt-dlp=ext=mp4 / vcodec=av01.0.08M.08 / acodec=mp4a.40.2", viewModel.CompletionInfoTooltip);
    }
}
