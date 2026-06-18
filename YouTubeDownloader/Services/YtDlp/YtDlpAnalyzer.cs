using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YouTubeDownloader.Models;

namespace YouTubeDownloader.Services;

internal sealed class YtDlpAnalyzer
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly YtDlpUpdater _updater;
    private readonly Func<string> _getYtDlpPath;

    public YtDlpAnalyzer(ISettingsRepository settingsRepository, YtDlpUpdater updater, Func<string> getYtDlpPath)
    {
        _settingsRepository = settingsRepository;
        _updater = updater;
        _getYtDlpPath = getYtDlpPath;
    }

    public async Task<YtDlpAnalyzeResult> AnalyzeUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            var ytDlpPath = _getYtDlpPath();
            await _updater.EnsureYtDlpUpdatedAsync(ytDlpPath, cancellationToken);
            var settings = _settingsRepository.Load();
            var metadataLanguageArgs = YtDlpArgumentBuilder.BuildMetadataLanguageArguments(settings.DefaultMetadataLanguage);

            // URLの種類を判定してから適切な方法で解析
            bool isPlaylistUrl = IsPlaylistUrl(url);

            if (isPlaylistUrl)
            {
                // プレイリストURL: --flat-playlistで高速に一覧取得
                var playlistArgs = new List<string>(metadataLanguageArgs)
                {
                    "--dump-single-json",
                    "--flat-playlist",
                    url
                };
                var playlistInfoResult = await YtDlpProcessRunner.RunAsync(ytDlpPath, playlistArgs, cancellationToken);
                if (!string.IsNullOrEmpty(playlistInfoResult))
                {
                    return YtDlpMetadataParser.ParsePlaylistFromJson(playlistInfoResult);
                }
            }

            // 単一動画として解析
            var videoArgs = new List<string>(metadataLanguageArgs)
            {
                "--dump-json",
                "--no-playlist",
                url
            };
            var (videoResult, videoError, _) = await YtDlpProcessRunner.RunRawAsync(
                ytDlpPath, videoArgs, cancellationToken);
            var video = !string.IsNullOrEmpty(videoResult) ? YtDlpMetadataParser.ParseVideoMetadata(videoResult, url) : null;

            // 失敗時はplayer clientを変えて再試行（UNPLAYABLE/403などはこれで取得できることがある）
            if (video == null)
            {
                var fallbackArgs = YtDlpArgumentBuilder.BuildMetadataLanguageArguments(
                    settings.DefaultMetadataLanguage,
                    YtDlpArgumentBuilder.FallbackPlayerClients);
                var retryArgs = new List<string>(fallbackArgs)
                {
                    "--dump-json",
                    "--no-playlist",
                    url
                };
                var (retryResult, retryError, _) = await YtDlpProcessRunner.RunRawAsync(
                    ytDlpPath, retryArgs, cancellationToken);
                if (!string.IsNullOrEmpty(retryResult))
                {
                    video = YtDlpMetadataParser.ParseVideoMetadata(retryResult, url);
                }

                // 再試行のエラーを優先（無ければ初回のエラー）して理由を残す
                if (!string.IsNullOrWhiteSpace(retryError))
                {
                    videoError = retryError;
                }
            }

            if (video != null)
            {
                return new YtDlpAnalyzeResult
                {
                    IsSuccess = true,
                    IsPlaylist = false,
                    VideoMetadata = video
                };
            }

            // 実際のyt-dlpのエラー理由を添えて返す（文字化けはStdErrEncodingで解消済み）
            var reason = YtDlpFailureFormatter.ExtractMeaningfulError(videoError);
            return new YtDlpAnalyzeResult
            {
                IsSuccess = false,
                ErrorMessage = $"動画情報を取得できませんでした。\n{reason}"
            };
        }
        catch (Exception ex)
        {
            return new YtDlpAnalyzeResult
            {
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private static bool IsPlaylistUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.AbsolutePath.Contains("/playlist", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var query = uri.Query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = part.IndexOf('=');
            var key = separatorIndex >= 0 ? part[..separatorIndex] : part;
            if (Uri.UnescapeDataString(key).Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
