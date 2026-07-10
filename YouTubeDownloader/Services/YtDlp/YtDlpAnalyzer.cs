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
            // 認証cookie（任意）。解析でもログインが必要な動画を扱えるよう全試行に付与する。
            var cookieArgs = YtDlpArgumentBuilder.BuildCookieArguments(settings);

            // URLの種類を判定してから適切な方法で解析
            bool isPlaylistUrl = IsPlaylistUrl(url);

            if (isPlaylistUrl)
            {
                // プレイリストURL: --flat-playlistで高速に一覧取得
                var playlistArgs = new List<string>(metadataLanguageArgs);
                playlistArgs.AddRange(cookieArgs);
                playlistArgs.AddRange(new[] { "--dump-single-json", "--flat-playlist", url });
                var playlistInfoResult = await YtDlpProcessRunner.RunAsync(ytDlpPath, playlistArgs, cancellationToken);
                if (!string.IsNullOrEmpty(playlistInfoResult))
                {
                    return YtDlpMetadataParser.ParsePlaylistFromJson(playlistInfoResult);
                }
            }

            // 単一動画として解析（初回から tv 等の安定clientを使い、既定clientの初回失敗を避ける）
            var primaryClientArgs = YtDlpArgumentBuilder.BuildMetadataLanguageArguments(
                settings.DefaultMetadataLanguage,
                YtDlpArgumentBuilder.PrimaryPlayerClients);
            var videoArgs = new List<string>(primaryClientArgs);
            videoArgs.AddRange(cookieArgs);
            videoArgs.AddRange(new[] { "--dump-json", "--no-playlist", url });
            var (videoResult, videoError, _) = await YtDlpProcessRunner.RunRawAsync(
                ytDlpPath, videoArgs, cancellationToken);
            var video = !string.IsNullOrEmpty(videoResult) ? YtDlpMetadataParser.ParseVideoMetadata(videoResult, url) : null;

            // 失敗時はplayer clientを変えて再試行（UNPLAYABLE/403などはこれで取得できることがある）。
            // ただし bot検知・年齢制限・非公開・429 など回復不能なエラーは、別clientでも直らず、
            // 無駄な再アクセスがIP制限（bot判定）を悪化させるため再試行しない。
            if (video == null && YtDlpErrorClassifier.IsRetryWorthwhile(videoError))
            {
                var fallbackArgs = YtDlpArgumentBuilder.BuildMetadataLanguageArguments(
                    settings.DefaultMetadataLanguage,
                    YtDlpArgumentBuilder.FallbackPlayerClients);
                var retryArgs = new List<string>(fallbackArgs);
                retryArgs.AddRange(cookieArgs);
                retryArgs.AddRange(new[] { "--dump-json", "--no-playlist", url });
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

            // 実際のyt-dlpのエラー理由を、既知パターンは正確な原因・対処に翻訳して添える（文字化けはStdErrEncodingで解消済み）
            var reason = YtDlpFailureFormatter.DescribeError(videoError);
            return new YtDlpAnalyzeResult
            {
                IsSuccess = false,
                ErrorMessage = $"動画情報を取得できませんでした。\n{reason}"
            };
        }
        catch (OperationCanceledException)
        {
            throw;
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
