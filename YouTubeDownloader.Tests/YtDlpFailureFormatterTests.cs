using YouTubeDownloader.Services;

namespace YouTubeDownloader.Tests;

public sealed class YtDlpFailureFormatterTests
{
    [Fact]
    public void FormatArgumentsForLog_RedactsSensitiveValuesAndUrlQuery()
    {
        var formatted = YtDlpFailureFormatter.FormatArgumentsForLog(new[]
        {
            "--cookies",
            @"C:\secret\cookies.txt",
            "--password",
            "password-value",
            "https://example.test/video?token=secret"
        });

        Assert.DoesNotContain("cookies.txt", formatted);
        Assert.DoesNotContain("password-value", formatted);
        Assert.DoesNotContain("token=secret", formatted);
        Assert.Equal(3, formatted.Split("[REDACTED]").Length - 1);
    }

    [Fact]
    public void RedactUrlForLog_LeavesUrlWithoutQueryUnchanged()
    {
        const string url = "https://example.test/video";
        Assert.Equal(url, YtDlpFailureFormatter.RedactUrlForLog(url));
    }
}
