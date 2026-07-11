using YouTubeDownloader.Models;
using YouTubeDownloader.Services;

namespace YouTubeDownloader.Tests;

public sealed class MetadataRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ytdl-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveFailure_DoesNotPublishUnpersistedMetadataToCache()
    {
        Directory.CreateDirectory(_directory);
        var repository = new MetadataRepository(
            Path.Combine(_directory, "metadata.json"),
            new TestLogger(),
            (_, _) => Task.FromException(new IOException("write failed")));

        await Assert.ThrowsAsync<IOException>(() => repository.SaveVideoMetadataAsync(
            new VideoMetadata { Id = "video-1", Title = "test" }));

        var videos = await repository.FindVideosAsync();
        Assert.Empty(videos);
    }

    [Fact]
    public async Task DeleteFailure_LeavesExistingMetadataVisible()
    {
        Directory.CreateDirectory(_directory);
        var failWrites = false;
        var repository = new MetadataRepository(
            Path.Combine(_directory, "metadata.json"),
            new TestLogger(),
            async (path, contents) =>
            {
                if (failWrites)
                {
                    throw new IOException("write failed");
                }
                await File.WriteAllTextAsync(path, contents);
            });

        await repository.SaveVideoMetadataAsync(new VideoMetadata { Id = "video-1", Title = "test" });
        failWrites = true;

        await Assert.ThrowsAsync<IOException>(() => repository.DeleteVideoMetadataAsync("video-1"));
        Assert.Single(await repository.FindVideosAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
