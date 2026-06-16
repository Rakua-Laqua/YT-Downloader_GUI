using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YouTubeDownloader.Models;

namespace YouTubeDownloader.Services;

/// <summary>
/// yt-dlpとの連携を行うクライアント
/// </summary>
public interface IYtDlpClient
{
    Task<YtDlpAnalyzeResult> AnalyzeUrlAsync(string url, CancellationToken cancellationToken = default);
    Task<YtDlpUpdateResult> UpdateYtDlpAsync(string? channelOverride = null, CancellationToken cancellationToken = default);
    Task<string?> GetYtDlpVersionAsync(CancellationToken cancellationToken = default);
    Task DownloadAsync(DownloadJob job, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken = default);
}

public class YtDlpUpdateResult
{
    public bool IsSuccess { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
}

public class ProgressInfo
{
    public int Percentage { get; set; }
    public string? Status { get; set; }
    public string? Speed { get; set; }
    public string? Eta { get; set; }

    /// <summary>変換・埋め込みなどの後処理フェーズ（パーセンテージが取れない区間）かどうか</summary>
    public bool IsPostProcessing { get; set; }
}

public class YtDlpClient : IYtDlpClient
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly ILoggingService _logger;
    private static string? _cachedYtDlpPath;
    private static string? _cachedFfmpegPath;
    private static string? _cachedYtDlpVersion;
    private readonly SemaphoreSlim _ytDlpUpdateLock = new(1, 1);
    private bool _hasCheckedYtDlpUpdate;

    /// <summary>
    /// yt-dlpの標準エラー出力をデコードするエンコーディング。
    /// yt-dlpはリダイレクト時、出力をシステムのANSIコードページ（日本語ならcp932）で書き出すため、
    /// UTF-8でデコードすると日本語のエラーメッセージが文字化けする。ANSIコードページで読む。
    /// </summary>
    private static readonly Encoding StdErrEncoding = ResolveStdErrEncoding();

    /// <summary>
    /// UNPLAYABLE/403などでデフォルトのclientが失敗した場合に再試行で使うplayer client群。
    /// </summary>
    private const string FallbackPlayerClients = "tv,web_safari,android";

    private static Encoding ResolveStdErrEncoding()
    {
        try
        {
            // .NET (Core)では既定でcp932等のコードページが使えないため、プロバイダを登録する
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var ansiCodePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            if (ansiCodePage > 0)
            {
                return Encoding.GetEncoding(ansiCodePage);
            }
        }
        catch
        {
            // 取得失敗時はUTF-8にフォールバック
        }
        return Encoding.UTF8;
    }

    public YtDlpClient(ISettingsRepository settingsRepository, ILoggingService logger)
    {
        _settingsRepository = settingsRepository;
        _logger = logger;
    }

    private string GetYtDlpPath()
    {
        var settings = _settingsRepository.Load();

        // 設定で指定されていればそれを使用
        if (!string.IsNullOrEmpty(settings.YtDlpPath) && File.Exists(settings.YtDlpPath))
        {
            return settings.YtDlpPath;
        }

        // キャッシュがあればそれを使用
        if (!string.IsNullOrEmpty(_cachedYtDlpPath) && File.Exists(_cachedYtDlpPath))
        {
            return _cachedYtDlpPath;
        }

        // 自動検出（検索ロジックは ExecutableLocator に集約）
        _cachedYtDlpPath = ExecutableLocator.FindExecutable("yt-dlp.exe", "yt-dlp");
        if (!string.IsNullOrEmpty(_cachedYtDlpPath))
        {
            return _cachedYtDlpPath;
        }

        throw new InvalidOperationException("yt-dlpが見つかりません。yt-dlpをインストールするか、設定画面でパスを指定してください。");
    }

    public static string? GetFfmpegPath()
    {
        // キャッシュがあればそれを使用
        if (!string.IsNullOrEmpty(_cachedFfmpegPath) && File.Exists(_cachedFfmpegPath))
        {
            return _cachedFfmpegPath;
        }

        // 自動検出
        _cachedFfmpegPath = ExecutableLocator.FindExecutable("ffmpeg.exe", "ffmpeg");
        return _cachedFfmpegPath;
    }

    public async Task<YtDlpAnalyzeResult> AnalyzeUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            var ytDlpPath = GetYtDlpPath();
            await EnsureYtDlpUpdatedAsync(ytDlpPath, cancellationToken);
            var settings = _settingsRepository.Load();
            var metadataLanguageArgs = BuildMetadataLanguageArguments(settings.DefaultMetadataLanguage);

            // URLの種類を判定してから適切な方法で解析
            bool isPlaylistUrl = url.Contains("list=") || url.Contains("/playlist");

            if (isPlaylistUrl)
            {
                // プレイリストURL: --flat-playlistで高速に一覧取得
                var playlistArgs = new List<string>(metadataLanguageArgs)
                {
                    "--dump-single-json",
                    "--flat-playlist",
                    url
                };
                var playlistInfoResult = await RunYtDlpAsync(ytDlpPath, playlistArgs, cancellationToken);
                if (!string.IsNullOrEmpty(playlistInfoResult))
                {
                    return ParsePlaylistFromJson(playlistInfoResult);
                }
            }

            // 単一動画として解析
            var videoArgs = new List<string>(metadataLanguageArgs)
            {
                "--dump-json",
                "--no-playlist",
                url
            };
            var (videoResult, videoError, _) = await RunYtDlpRawAsync(
                ytDlpPath, videoArgs, cancellationToken);
            var video = !string.IsNullOrEmpty(videoResult) ? ParseVideoMetadata(videoResult, url) : null;

            // 失敗時はplayer clientを変えて再試行（UNPLAYABLE/403などはこれで取得できることがある）
            if (video == null)
            {
                var fallbackArgs = BuildMetadataLanguageArguments(settings.DefaultMetadataLanguage, FallbackPlayerClients);
                var retryArgs = new List<string>(fallbackArgs)
                {
                    "--dump-json",
                    "--no-playlist",
                    url
                };
                var (retryResult, retryError, _) = await RunYtDlpRawAsync(
                    ytDlpPath, retryArgs, cancellationToken);
                if (!string.IsNullOrEmpty(retryResult))
                {
                    video = ParseVideoMetadata(retryResult, url);
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
            var reason = ExtractMeaningfulError(videoError);
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

    private static YtDlpAnalyzeResult ParsePlaylistFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var playlist = new PlaylistMetadata
            {
                Id = GetStringProperty(root, "id") ?? "",
                Title = GetStringProperty(root, "title") ?? "プレイリスト",
                Channel = GetStringProperty(root, "uploader") ?? GetStringProperty(root, "channel") ?? "",
                ThumbnailUrl = GetThumbnailUrl(root)
            };

            var videos = new List<VideoMetadata>();

            if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                int index = 1;
                foreach (var entry in entries.EnumerateArray())
                {
                    var videoId = GetStringProperty(entry, "id") ?? "";
                    var video = new VideoMetadata
                    {
                        Id = videoId,
                        Title = GetStringProperty(entry, "title") ?? $"動画 {index}",
                        Channel = GetStringProperty(entry, "uploader") ?? GetStringProperty(entry, "channel") ?? playlist.Channel,
                        DurationSeconds = GetIntProperty(entry, "duration"),
                        ThumbnailUrl = GetThumbnailUrl(entry),
                        // 常にYouTube動画URLを正しく構築
                        Url = !string.IsNullOrEmpty(videoId) ? $"https://www.youtube.com/watch?v={videoId}" : "",
                        PlaylistId = playlist.Id,
                        PlaylistIndex = index
                    };
                    videos.Add(video);
                    index++;
                }
            }

            playlist.Videos = videos;
            playlist.VideoCount = videos.Count;

            // YouTube上の総数(playlist_count)。取得できなければ実取得数にそろえる。
            // 実取得数より大きい場合は「取得漏れ」(古いyt-dlpのページネーション不具合など)を意味する。
            var totalCount = GetIntProperty(root, "playlist_count");
            playlist.TotalVideoCount = totalCount > videos.Count ? totalCount : videos.Count;

            return new YtDlpAnalyzeResult
            {
                IsSuccess = true,
                IsPlaylist = true,
                PlaylistMetadata = playlist
            };
        }
        catch (Exception ex)
        {
            return new YtDlpAnalyzeResult
            {
                IsSuccess = false,
                ErrorMessage = $"プレイリスト解析エラー: {ex.Message}"
            };
        }
    }

    private static VideoMetadata? ParseVideoMetadata(string json, string url)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var publishDateStr = GetStringProperty(root, "upload_date");
            DateTime? publishDate = null;
            if (!string.IsNullOrEmpty(publishDateStr) && publishDateStr.Length == 8)
            {
                if (DateTime.TryParseExact(publishDateStr, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    publishDate = parsed;
                }
            }

            return new VideoMetadata
            {
                Id = GetStringProperty(root, "id") ?? "",
                Title = GetStringProperty(root, "title") ?? "",
                Channel = GetStringProperty(root, "uploader") ?? GetStringProperty(root, "channel") ?? "",
                DurationSeconds = GetIntProperty(root, "duration"),
                PublishDate = publishDate,
                ThumbnailUrl = GetThumbnailUrl(root),
                Url = GetStringProperty(root, "webpage_url") ?? url
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private static int GetIntProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number)
            {
                if (prop.TryGetInt32(out var intResult))
                {
                    return intResult;
                }

                if (prop.TryGetDouble(out var doubleResult))
                {
                    return ToInt32OrDefault(doubleResult);
                }
            }
            if (prop.ValueKind == JsonValueKind.String)
            {
                var value = prop.GetString();
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intResult))
                {
                    return intResult;
                }

                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleResult))
                {
                    return ToInt32OrDefault(doubleResult);
                }
            }
        }
        return 0;
    }

    private static int ToInt32OrDefault(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        if (value > int.MaxValue)
        {
            return int.MaxValue;
        }

        if (value < int.MinValue)
        {
            return int.MinValue;
        }

        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private static string GetThumbnailUrl(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        // thumbnailプロパティを優先
        if (element.TryGetProperty("thumbnail", out var thumb) && thumb.ValueKind == JsonValueKind.String)
        {
            return thumb.GetString() ?? "";
        }

        // thumbnails配列から取得
        if (element.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
        {
            JsonElement? bestThumb = null;
            int maxWidth = 0;

            foreach (var t in thumbs.EnumerateArray())
            {
                var width = GetIntProperty(t, "width");
                if (width > maxWidth || bestThumb == null)
                {
                    maxWidth = width;
                    bestThumb = t;
                }
            }

            if (bestThumb.HasValue)
            {
                return GetStringProperty(bestThumb.Value, "url") ?? "";
            }
        }

        return "";
    }

    private static List<string> BuildMetadataLanguageArguments(string? language, string? playerClient = null)
    {
        var extractorArgs = new List<string>();
        var normalized = NormalizeMetadataLanguage(language);
        if (!string.IsNullOrEmpty(normalized))
        {
            extractorArgs.Add($"lang={normalized}");
        }

        if (!string.IsNullOrWhiteSpace(playerClient))
        {
            extractorArgs.Add($"player_client={playerClient.Trim()}");
        }

        return extractorArgs.Count == 0
            ? new List<string>()
            : new List<string> { "--extractor-args", $"youtube:{string.Join(';', extractorArgs)}" };
    }

    private static string NormalizeMetadataLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return string.Empty;
        }

        var trimmed = language.Trim();
        if (trimmed.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        foreach (var c in trimmed)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                return string.Empty;
            }
        }

        return trimmed;
    }

    public async Task DownloadAsync(DownloadJob job, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken = default)
    {
        var jobLabel = BuildJobLabel(job);
        _logger.Info($"ダウンロード開始 {jobLabel} / 形式={job.Format} 品質={job.Quality} 保存先=\"{job.SaveFolderPath}\"");

        // URLが空の場合はエラー
        if (string.IsNullOrEmpty(job.VideoMetadata.Url))
        {
            const string msg = "動画URLが指定されていません";
            job.FailureDetail = BuildPreflightFailureDetail(job, "準備(URL検証)", msg, null);
            _logger.Error($"ダウンロード失敗 {jobLabel} / フェーズ=準備(URL検証): {msg}");
            throw new Exception(msg);
        }

        // 準備段階(実行ファイル検出・保存先作成・出力パス構築)は失敗フェーズが分かるよう個別に記録する
        string ytDlpPath;
        try
        {
            ytDlpPath = GetYtDlpPath();
        }
        catch (Exception ex)
        {
            job.FailureDetail = BuildPreflightFailureDetail(job, "準備(yt-dlp検出)", ex.Message, ex);
            _logger.Error($"ダウンロード失敗 {jobLabel} / フェーズ=準備(yt-dlp検出)", ex);
            throw;
        }

        await EnsureYtDlpUpdatedAsync(ytDlpPath, cancellationToken);
        var settings = _settingsRepository.Load();

        string outputPath;
        string requestedFormat;
        try
        {
            // 保存先フォルダを作成
            Directory.CreateDirectory(job.SaveFolderPath);

            // ファイル名テンプレートを構築
            var template = BuildFilenameTemplate(settings.FilenameTemplate, job);
            outputPath = BuildOutputPath(job.SaveFolderPath, template);

            requestedFormat = NormalizeFormat(job.Format);
        }
        catch (Exception ex)
        {
            job.FailureDetail = BuildPreflightFailureDetail(job, "準備(保存先/出力パス構築)", ex.Message, ex);
            _logger.Error($"ダウンロード失敗 {jobLabel} / フェーズ=準備(保存先/出力パス構築)", ex);
            throw;
        }

        // mp3 / wav は再エンコード(transcode)が走る。ffmpegに変換進捗をファイル出力させ、
        // 動画長と突き合わせて「音声変換中」の実進捗を出すための一時ファイルを用意する。
        // (m4aはコピーで一瞬／動画長が不明な場合は実進捗を出せないので対象外)
        var durationSeconds = job.VideoMetadata.DurationSeconds;
        string? conversionProgressFile = IsAudioFormat(requestedFormat) && requestedFormat != "m4a" && durationSeconds > 0
            ? Path.Combine(Path.GetTempPath(), $"ytdlp_ffprog_{job.Id:N}.txt")
            : null;

        // ダウンロードした正確なファイルパスと公開日時を書き出させる一時ファイル
        string downloadInfoFile = Path.Combine(Path.GetTempPath(), $"ytdlp_info_{job.Id:N}.txt");

        var arguments = BuildDownloadArguments(job, settings, outputPath, requestedFormat, conversionProgressFile, downloadInfoFile);

        try
        {
            _logger.Info($"yt-dlp 実行 {jobLabel}: {ytDlpPath} {FormatArgumentsForLog(arguments)}");
            var firstRun = await RunDownloadProcessAsync(ytDlpPath, arguments, progress, conversionProgressFile, durationSeconds, cancellationToken);

            if (firstRun.ExitCode != 0)
            {
                _logger.Warn($"yt-dlp 初回失敗 {jobLabel} / 終了コード={firstRun.ExitCode} 失敗フェーズ={firstRun.LastPhase} 経過={firstRun.Elapsed.TotalSeconds:F1}秒。フォールバックclientで再試行します。");

                // 403 や UNPLAYABLE（「この動画は…」）はデフォルトclientの問題であることが多い。
                // 別のplayer client群（tv等）で1回だけ再試行する。
                var retryArguments = BuildFallbackClientArguments(arguments, settings);

                _logger.Info($"yt-dlp 再試行 {jobLabel}: {ytDlpPath} {FormatArgumentsForLog(retryArguments)}");
                var retryRun = await RunDownloadProcessAsync(ytDlpPath, retryArguments, progress, conversionProgressFile, durationSeconds, cancellationToken);

                if (retryRun.ExitCode != 0)
                {
                    var firstReason = ExtractMeaningfulError(firstRun.StdErr);
                    var retryReason = ExtractMeaningfulError(retryRun.StdErr);

                    // 失敗の「どのタイミングで・なぜ」が後から追えるよう、両試行の終了コード・失敗フェーズ・
                    // 経過時間・実行コマンド・stderr全文・yt-dlpバージョンをまとめて記録する。
                    var ytDlpVersion = await GetYtDlpVersionForLogAsync(ytDlpPath);
                    var detail = BuildDownloadFailureDetail(job, ytDlpVersion, arguments, retryArguments, firstRun, retryRun);
                    job.FailureDetail = detail;
                    _logger.Error($"ダウンロード失敗 {jobLabel}{Environment.NewLine}{detail}");

                    var combinedReason = $"【初回試行のエラー】:\n{firstReason}\n\n【再試行（フォールバック）のエラー】:\n{retryReason}";
                    throw new Exception($"ダウンロードに失敗しました:\n{combinedReason}");
                }

                _logger.Info($"yt-dlp 再試行成功 {jobLabel} / 経過={retryRun.Elapsed.TotalSeconds:F1}秒");
            }

            var infoResult = ReadDownloadInfoFile(downloadInfoFile);

            // 1) yt-dlpが書き出した正確なファイルパスがある場合、それを最優先で使用する
            if (!string.IsNullOrEmpty(infoResult.FilePath) && File.Exists(infoResult.FilePath))
            {
                job.VideoMetadata.LocalFilePath = infoResult.FilePath;
            }
            else
            {
                // 2) 取得できなかった場合のフォールバックとして、従来の走査（推測）を行う
                UpdateLocalFilePath(job, outputPath, requestedFormat);
            }

            // 更新日時のみ公開時刻に合わせる（作成日時はダウンロード時刻のまま残す）
            if (settings.SetFileDateToPublishDate
                && !string.IsNullOrEmpty(job.VideoMetadata.LocalFilePath)
                && File.Exists(job.VideoMetadata.LocalFilePath)
                && infoResult.PublishTime.HasValue)
            {
                var publishTime = infoResult.PublishTime.Value;
                // timestamp由来はUTC絶対時刻なのでSetLastWriteTimeUtcで保存する。
                // こうするとWindowsは閲覧側PCのローカル時刻に変換して表示する。
                // upload_date由来は時刻が無いためローカル0時として保存する。
                if (publishTime.Kind == DateTimeKind.Utc)
                {
                    File.SetLastWriteTimeUtc(job.VideoMetadata.LocalFilePath, publishTime);
                }
                else
                {
                    File.SetLastWriteTime(job.VideoMetadata.LocalFilePath, publishTime);
                }
            }

            _logger.Info($"ダウンロード成功 {jobLabel} / 出力=\"{job.VideoMetadata.LocalFilePath}\"");
        }
        finally
        {
            if (conversionProgressFile != null)
            {
                try
                {
                    if (File.Exists(conversionProgressFile))
                    {
                        File.Delete(conversionProgressFile);
                    }
                }
                catch
                {
                    // 一時ファイルの削除失敗は無視
                }
            }

            if (downloadInfoFile != null)
            {
                try
                {
                    if (File.Exists(downloadInfoFile))
                    {
                        File.Delete(downloadInfoFile);
                    }
                }
                catch
                {
                    // 一時ファイルの削除失敗は無視
                }
            }
        }
    }

    /// <summary>
    /// yt-dlpが書き出した公開時刻の一時ファイル（"timestamp|upload_date" 形式）を読み取る。
    /// timestamp(UNIX秒)があれば分・秒まで含むUTC絶対時刻(Kind=Utc)を返す。
    /// 無ければ upload_date(YYYYMMDD) のローカル0時(Kind=Unspecified)を返す。
    /// 再試行で複数行になり得るため、最初に解釈できた行を採用する。
    /// </summary>
    private class DownloadInfoResult
    {
        public string? FilePath { get; set; }
        public DateTime? PublishTime { get; set; }
    }

    private static DownloadInfoResult ReadDownloadInfoFile(string path)
    {
        var result = new DownloadInfoResult();
        try
        {
            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    var parts = line.Trim().Split('|');
                    if (parts.Length >= 1)
                    {
                        var filePathVal = parts[0].Trim();
                        if (!string.IsNullOrEmpty(filePathVal))
                        {
                            result.FilePath = filePathVal;
                        }
                    }

                    // 1) timestamp(UNIX秒) を最優先
                    if (parts.Length >= 2
                        && long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
                        && epoch > 0)
                    {
                        result.PublishTime = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime; // Kind=Utc
                    }
                    // 2) upload_date(YYYYMMDD) にフォールバック
                    else if (parts.Length >= 3)
                    {
                        var dateToken = parts[2].Trim();
                        if (dateToken.Length == 8
                            && DateTime.TryParseExact(dateToken, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                        {
                            result.PublishTime = parsedDate; // Kind=Unspecified
                        }
                    }

                    if (result.FilePath != null || result.PublishTime != null)
                    {
                        break;
                    }
                }
            }
        }
        catch
        {
            // 読み取り失敗時は何も設定しない
        }
        return result;
    }

    /// <summary>
    /// ダウンロード用のyt-dlpコマンドライン引数を構築する
    /// </summary>
    private static List<string> BuildDownloadArguments(DownloadJob job, AppSettings settings, string outputPath, string requestedFormat, string? conversionProgressFile = null, string? downloadInfoFile = null)
    {
        var args = new List<string>();

        // タイトル等のローカライズは設定画面の「タイトル取得言語」を優先する
        // 出力テンプレート
        args.Add("-o");
        args.Add(outputPath);
        args.Add("--force-overwrites");
        args.AddRange(BuildMetadataLanguageArguments(settings.DefaultMetadataLanguage));

        // フォーマット指定
        if (IsAudioFormat(requestedFormat))
        {
            if (requestedFormat == "m4a")
            {
                // m4aネイティブ(AAC)音源を最優先で選択する。
                // 元がAACなら ExtractAudio が再エンコードせずコピーするため変換がほぼ一瞬で終わる。
                // (--audio-quality はコピー時には無視されるため付与しない)
                args.Add("-f");
                args.Add("bestaudio[ext=m4a]/bestaudio/best");
                args.Add("-x");
                args.Add("--audio-format");
                args.Add("m4a");
            }
            else
            {
                // mp3 / wav は仕様上どうしても再エンコード(transcode)が発生する。
                args.Add("-f");
                args.Add("bestaudio/best");
                args.Add("-x");
                args.Add("--audio-format");
                args.Add(requestedFormat);
                args.Add("--audio-quality");
                args.Add(BuildAudioQualityArgument(job.Quality));

                // ExtractAudio(ffmpeg)に変換進捗を一時ファイルへ出力させ、実進捗を表示できるようにする。
                // yt-dlpは --postprocessor-args の値を shlex 分割する際にバックスラッシュをエスケープとして
                // 食ってしまうため、ffmpegへ渡すパスはスラッシュ区切りにする（Windowsでも有効）。
                if (!string.IsNullOrEmpty(conversionProgressFile))
                {
                    var ffmpegProgressPath = conversionProgressFile.Replace('\\', '/');
                    args.Add("--postprocessor-args");
                    args.Add($"ExtractAudio:-progress \"{ffmpegProgressPath}\"");
                }
            }
        }
        else
        {
            args.Add("-f");
            args.Add(BuildVideoFormatSelector(job.Quality, requestedFormat, settings.PreferHighEfficiencyCodecs));
            args.Add("--merge-output-format");
            args.Add(requestedFormat);
        }

        // メタデータを埋め込む
        args.Add("--embed-metadata");

        // メタデータの「年」タグを公開年に修正する。
        // yt-dlpが既定で書く date=YYYYMMDD(8桁) はWindowsの「年」が16bitに丸めて化けるため、
        // upload_date の年(4桁)だけを meta_date に上書きして埋め込ませる。
        // yt-dlp自身が抽出する upload_date を使うので、フラット解析で PublishDate が空になる
        // プレイリスト項目でも確実に効く。
        if (settings.FixMetadataYear)
        {
            args.Add("--parse-metadata");
            args.Add("%(upload_date>%Y)s:%(meta_date)s");
        }

        // ファイル更新日時を公開時刻に合わせる場合、yt-dlpの公開時刻を一時ファイルへ書き出し、
        // ダウンロード完了後にC#側で読み取って設定する。
        // timestamp(UNIX秒, UTC絶対時刻) を優先し、無い動画は upload_date(日付のみ) にフォールバックする。
        // after_move: は最終ファイル移動後に走り、--print と違い --simulate を誘発しない。
        if (!string.IsNullOrEmpty(downloadInfoFile))
        {
            args.Add("--print-to-file");
            args.Add("after_move:%(filepath)s|%(timestamp)s|%(upload_date)s");
            args.Add(downloadInfoFile);
        }

        // サムネイルを埋め込む（音声ファイルの場合はアートワークとして）
        if (requestedFormat == "mp3" || requestedFormat == "m4a")
        {
            args.Add("--embed-thumbnail");
        }

        // ffmpegパスを自動検出または設定から取得
        var ffmpegPath = !string.IsNullOrEmpty(settings.FfmpegPath) && File.Exists(settings.FfmpegPath)
            ? settings.FfmpegPath
            : GetFfmpegPath();

        if (!string.IsNullOrEmpty(ffmpegPath))
        {
            var ffmpegDir = Path.GetDirectoryName(ffmpegPath);
            if (!string.IsNullOrEmpty(ffmpegDir))
            {
                args.Add("--ffmpeg-location");
                args.Add(ffmpegDir);
            }
        }

        // YouTube対策: User-Agent は指定するが、player_client=web の強制は
        // SABR でURLが欠落し「Requested format is not available」になり得るため行わない。
        args.Add("--user-agent");
        args.Add("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        // 分割配信(DASH/HLS)のフラグメントを並列ダウンロードして取得を高速化する。
        // mp3/wav は再エンコードが避けられないぶん、ダウンロード時間を詰めて総時間を短縮する。
        args.Add("--concurrent-fragments");
        args.Add("4");

        // 進捗表示用
        args.Add("--newline");
        args.Add("--no-warnings");

        // JSチャレンジ解決用のランタイムを指定（DenoとNode.jsを優先）
        // --js-runtimes はカンマ区切り不可。runtimeごとに指定する必要がある。
        // 未インストールのランタイムは警告なしでスキップされ、使えるものへ順にフォールバックする。
        args.Add("--js-runtimes");
        args.Add("deno");
        args.Add("--js-runtimes");
        args.Add("node");

        // URL
        args.Add(job.VideoMetadata.Url);

        return args;
    }

    /// <summary>
    /// 初回の引数の --extractor-args を player_client=tv等 付きに差し替えたリトライ用引数を構築する
    /// </summary>
    private static List<string> BuildFallbackClientArguments(IReadOnlyList<string> arguments, AppSettings settings)
    {
        var retryArgs = new List<string>(arguments);
        var retryMetadataArgs = BuildMetadataLanguageArguments(settings.DefaultMetadataLanguage, FallbackPlayerClients);

        if (retryMetadataArgs.Count == 0)
        {
            return retryArgs;
        }

        var extractorArgsIndex = retryArgs.IndexOf("--extractor-args");
        if (extractorArgsIndex >= 0 && extractorArgsIndex + 1 < retryArgs.Count)
        {
            retryArgs[extractorArgsIndex + 1] = retryMetadataArgs[1];
        }
        else
        {
            retryArgs.AddRange(retryMetadataArgs);
        }

        return retryArgs;
    }

    private static string BuildAudioQualityArgument(string quality)
    {
        var normalized = quality.Trim();
        return normalized switch
        {
            "最高 (VBR 0)" => "0",
            "高音質 (VBR 2)" => "2",
            "標準 (VBR 5)" => "5",
            "軽量 (VBR 7)" => "7",
            "最小 (VBR 10)" => "10",
            _ => NormalizeAudioQualityArgument(normalized)
        };
    }

    private static string NormalizeAudioQualityArgument(string quality)
    {
        if (int.TryParse(quality, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vbrQuality) &&
            vbrQuality is >= 0 and <= 10)
        {
            return vbrQuality.ToString(CultureInfo.InvariantCulture);
        }

        var bitrate = quality.ToUpperInvariant();
        if (bitrate.EndsWith('K') &&
            int.TryParse(bitrate[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kbps) &&
            kbps > 0)
        {
            return $"{kbps}K";
        }

        return "5";
    }

    /// <summary>
    /// yt-dlpのダウンロードプロセスを起動し、stdoutから進捗を報告しながら終了コードとstderrを返す。
    /// 初回実行と403リトライで共通。
    /// </summary>
    private static async Task<YtDlpRunResult> RunDownloadProcessAsync(
        string ytDlpPath,
        IEnumerable<string> arguments,
        IProgress<ProgressInfo>? progress,
        string? conversionProgressFile,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        var psi = CreateYtDlpStartInfo(ytDlpPath, arguments);

        // 失敗時に「どのタイミングで落ちたか」を記録するため、実行時間を計測する
        var stopwatch = Stopwatch.StartNew();

        using var process = new Process { StartInfo = psi };
        process.Start();
        using var killRegistration = RegisterProcessKillOnCancellation(process, cancellationToken);

        // 変換進捗ポーラーのキャンセル制御（ExtractAudio開始時に起動する）
        using var conversionPollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? conversionPollTask = null;
        var canTrackConversion = !string.IsNullOrEmpty(conversionProgressFile) && durationSeconds > 0;

        // 失敗時に「どのフェーズで落ちたか」を残すため、最後に観測したフェーズを追跡する。
        // 書き込みは outputTask 内のみで、読み取りは Task.WhenAll 完了後に行う（完了により可視性は保証される）。
        var lastPhase = "(処理開始前)";

        // 出力を読み取りながら進捗を報告
        var outputTask = Task.Run(async () =>
        {
            string currentPhaseStatus = "動画データダウンロード中"; // デフォルト
            while (!process.StandardOutput.EndOfStream)
            {
                var line = await process.StandardOutput.ReadLineAsync();
                if (line == null)
                {
                    continue;
                }

                // 現在フェーズを判定し、失敗フェーズ追跡と進捗の現在フェーズ表示を更新する
                var detectedPhase = DetectPhaseName(line);
                if (detectedPhase != null)
                {
                    lastPhase = detectedPhase;
                    // ダウンロード(動画/音声)の判定のみ進捗メッセージへ反映する
                    if (detectedPhase.Contains("ダウンロード"))
                    {
                        currentPhaseStatus = detectedPhase;
                    }
                }

                // 音声変換(ExtractAudio)開始を検知したら、ffmpegの進捗ファイルのポーリングを開始する。
                // ポーラーが実進捗(%)を報告するので、この行自体は不確定表示にせず実進捗に委ねる。
                if (canTrackConversion && conversionPollTask == null &&
                    line.Contains("[ExtractAudio] Destination:"))
                {
                    conversionPollTask = PollConversionProgressAsync(conversionProgressFile!, durationSeconds, progress, conversionPollCts.Token);
                    progress?.Report(new ProgressInfo { Percentage = 0, Status = "音声変換中...", IsPostProcessing = false });
                    continue;
                }

                if (progress != null)
                {
                    var progressInfo = ParseProgressLine(line);
                    if (progressInfo != null)
                    {
                        // デフォルトのメッセージを現在フェーズに上書き
                        if (progressInfo.Status == "ダウンロード中")
                        {
                            progressInfo.Status = currentPhaseStatus;
                        }
                        progress.Report(progressInfo);
                    }
                }
            }
        });

        var errorOutput = new StringBuilder();
        var errorTask = Task.Run(async () =>
        {
            while (!process.StandardError.EndOfStream)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (line != null)
                {
                    errorOutput.AppendLine(line);
                }
            }
        });

        await Task.WhenAll(outputTask, errorTask);

        // 変換ポーラーを停止して終了を待つ
        conversionPollCts.Cancel();
        if (conversionPollTask != null)
        {
            try
            {
                await conversionPollTask;
            }
            catch
            {
                // ポーラーの後始末で例外は無視
            }
        }

        await process.WaitForExitAsync(CancellationToken.None);
        stopwatch.Stop();
        cancellationToken.ThrowIfCancellationRequested();

        return new YtDlpRunResult
        {
            ExitCode = process.ExitCode,
            StdErr = errorOutput.ToString(),
            LastPhase = lastPhase,
            Elapsed = stopwatch.Elapsed
        };
    }

    /// <summary>
    /// yt-dlpのstdout 1行から現在のフェーズ名を推定する。判定できない行は null を返す。
    /// 失敗時に「どのタイミングで落ちたか」を残すために使う。
    /// </summary>
    private static string? DetectPhaseName(string line)
    {
        if (line.Contains("[download] Destination:"))
        {
            var lowerLine = line.ToLowerInvariant();
            var isAudio = lowerLine.Contains(".m4a")
                || lowerLine.Contains(".mp3")
                || lowerLine.Contains(".opus")
                || lowerLine.Contains(".wav")
                || lowerLine.Contains(".f140")
                || lowerLine.Contains(".f251")
                || lowerLine.Contains(".f250")
                || lowerLine.Contains(".f249");
            return isAudio ? "音声データダウンロード中" : "動画データダウンロード中";
        }

        if (line.Contains("[ExtractAudio]")) return "音声変換中";
        if (line.Contains("[Merger]")) return "マージ中";
        if (line.Contains("[EmbedThumbnail]")) return "サムネイル埋め込み中";
        if (line.Contains("[Metadata]") || line.Contains("[EmbedMetadata]")) return "メタデータ書き込み中";
        return null;
    }

    private sealed class YtDlpRunResult
    {
        public int ExitCode { get; set; }
        public string StdErr { get; set; } = string.Empty;
        public string LastPhase { get; set; } = "(不明)";
        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>
    /// ExtractAudio(ffmpeg)が出力する進捗ファイルを定期的に読み、動画長と突き合わせて変換進捗(%)を報告する。
    /// </summary>
    private static async Task PollConversionProgressAsync(
        string progressFile,
        int durationSeconds,
        IProgress<ProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        if (progress == null || durationSeconds <= 0)
        {
            return;
        }

        var totalMicroseconds = durationSeconds * 1_000_000.0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(300, cancellationToken);

                var (outTimeMicroseconds, ended) = ReadFfmpegProgress(progressFile);
                if (outTimeMicroseconds > 0)
                {
                    var percent = (int)Math.Clamp(outTimeMicroseconds / totalMicroseconds * 100.0, 0, 99);
                    progress.Report(new ProgressInfo { Percentage = percent, Status = "音声変換中...", IsPostProcessing = false });
                }

                if (ended)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常な停止
        }
        catch
        {
            // 進捗ファイルの読み取り失敗は無視（実進捗が出ないだけ）
        }
    }

    /// <summary>
    /// ffmpegの -progress 出力ファイルから最新の out_time(マイクロ秒) と終了フラグを読み取る。
    /// </summary>
    private static (long OutTimeMicroseconds, bool Ended) ReadFfmpegProgress(string progressFile)
    {
        try
        {
            using var stream = new FileStream(progressFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            long lastOutTime = 0;
            var ended = false;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
                {
                    if (long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0)
                    {
                        lastOutTime = value;
                    }
                }
                else if (line.StartsWith("progress=end", StringComparison.Ordinal))
                {
                    ended = true;
                }
            }

            return (lastOutTime, ended);
        }
        catch
        {
            return (0, false);
        }
    }

    /// <summary>
    /// ダウンロード完了後、出力テンプレートに一致する実ファイルを探してジョブに記録する
    /// </summary>
    private static void UpdateLocalFilePath(DownloadJob job, string outputPath, string requestedFormat)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            outputDirectory = job.SaveFolderPath;
        }

        // 注意: Path.GetFileNameWithoutExtension は "Ep.01" の ".01" を拡張子として落とし、
        // 検索パターンが "…Ep.*" に広がって別エピソードを掴む原因になる。
        // %(ext)s か既知のメディア拡張子で終わる時だけ取り除き、それ以外は名前全体をベースにする。
        var outputFileName = Path.GetFileName(outputPath);
        string outputBaseName;
        if (outputFileName.EndsWith(".%(ext)s", StringComparison.OrdinalIgnoreCase))
        {
            outputBaseName = outputFileName[..^".%(ext)s".Length];
        }
        else if (HasKnownMediaExtension(outputFileName))
        {
            outputBaseName = outputFileName[..^Path.GetExtension(outputFileName).Length];
        }
        else
        {
            outputBaseName = outputFileName;
        }

        var files = Directory.GetFiles(outputDirectory, $"{Path.GetFileName(outputBaseName)}.*")
            .Select(path => new FileInfo(path))
            .Where(file => IsLikelyDownloadedMedia(file, requestedFormat))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();

        var file = files.FirstOrDefault();
        if (file != null)
        {
            job.VideoMetadata.LocalFilePath = file.FullName;
        }
    }

    private static bool IsLikelyDownloadedMedia(FileInfo file, string requestedFormat)
    {
        var extension = file.Extension.TrimStart('.').ToLowerInvariant();
        if (extension == requestedFormat)
        {
            return true;
        }

        return extension is "mp4" or "mkv" or "webm" or "mp3" or "m4a" or "wav";
    }

    /// <summary>
    /// ファイル名（またはテンプレート）が既知のメディア拡張子で終わるかを判定する。
    /// Path.HasExtension は "Ep.01" の ".01" を拡張子と誤認するため、その代わりに使う。
    /// </summary>
    private static bool HasKnownMediaExtension(string name)
    {
        var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        return extension is "mp4" or "mkv" or "webm" or "mp3" or "m4a" or "wav";
    }

    private static string NormalizeFormat(string format)
    {
        var normalized = format.Trim().ToLowerInvariant();
        return IsAudioFormat(normalized) || normalized is "mp4" or "mkv" or "webm"
            ? normalized
            : "mp4";
    }

    private static bool IsAudioFormat(string format)
    {
        return format is "mp3" or "m4a" or "wav";
    }

    private static string BuildVideoFormatSelector(string quality, string requestedFormat, bool preferHighEfficiencyCodecs)
    {
        return requestedFormat == "mp4"
            ? BuildMp4VideoFormatSelector(quality, preferHighEfficiencyCodecs)
            : BuildGeneralVideoFormatSelector(quality);
    }

    private static string BuildMp4VideoFormatSelector(string quality, bool preferHighEfficiencyCodecs)
    {
        return quality switch
        {
            "1080p" => BuildMp4SelectorForHeights(1080, 720, preferHighEfficiencyCodecs),
            "720p" => BuildMp4SelectorForHeights(720, 480, preferHighEfficiencyCodecs),
            "480p" => BuildMp4SelectorForHeights(480, 360, preferHighEfficiencyCodecs),
            "360p" => BuildMp4SelectorForHeights(360, 360, preferHighEfficiencyCodecs),
            _ => BuildBestMp4Selector(preferHighEfficiencyCodecs)
        };
    }

    private static string BuildBestMp4Selector(bool preferHighEfficiencyCodecs)
    {
        var compatibleSelector =
            "bv*[ext=mp4][vcodec^=avc1]+ba[ext=m4a]/" +
            "bv*[ext=mp4]+ba[ext=m4a]/" +
            "b[ext=mp4]/bv*+ba/b";

        return preferHighEfficiencyCodecs
            ? $"bv*[ext=mp4][vcodec^=av01]+ba[ext=m4a]/{compatibleSelector}"
            : compatibleSelector;
    }

    private static string BuildMp4SelectorForHeights(int targetHeight, int fallbackMinHeight, bool preferHighEfficiencyCodecs)
    {
        var compatibleExactHeightSelector =
            $"bv*[height={targetHeight}][ext=mp4][vcodec^=avc1]+ba[ext=m4a]/" +
            $"bv*[height={targetHeight}][ext=mp4]+ba[ext=m4a]/" +
            $"b[height={targetHeight}][ext=mp4]";

        var exactHeightSelector = preferHighEfficiencyCodecs
            ? $"bv*[height={targetHeight}][ext=mp4][vcodec^=av01]+ba[ext=m4a]/{compatibleExactHeightSelector}"
            : compatibleExactHeightSelector;

        if (fallbackMinHeight >= targetHeight)
        {
            return exactHeightSelector;
        }

        var compatibleFallbackSelector =
            $"bv*[height<={targetHeight}][height>={fallbackMinHeight}][ext=mp4][vcodec^=avc1]+ba[ext=m4a]/" +
            $"bv*[height<={targetHeight}][height>={fallbackMinHeight}][ext=mp4]+ba[ext=m4a]/" +
            $"b[height<={targetHeight}][height>={fallbackMinHeight}][ext=mp4]";

        var fallbackSelector = preferHighEfficiencyCodecs
            ? $"bv*[height<={targetHeight}][height>={fallbackMinHeight}][ext=mp4][vcodec^=av01]+ba[ext=m4a]/{compatibleFallbackSelector}"
            : compatibleFallbackSelector;

        return $"{exactHeightSelector}/{fallbackSelector}";
    }

    private static string BuildGeneralVideoFormatSelector(string quality)
    {
        return quality switch
        {
            "1080p" => "bv*[height=1080]+ba/b[height=1080]/bv*[height<=1080][height>=720]+ba/b[height<=1080][height>=720]",
            "720p" => "bv*[height=720]+ba/b[height=720]/bv*[height<=720][height>=480]+ba/b[height<=720][height>=480]",
            "480p" => "bv*[height=480]+ba/b[height=480]/bv*[height<=480][height>=360]+ba/b[height<=480][height>=360]",
            "360p" => "bv*[height=360]+ba/b[height=360]/b[height=360]",
            _ => "bv*+ba/b"
        };
    }

    public async Task<YtDlpUpdateResult> UpdateYtDlpAsync(string? channelOverride = null, CancellationToken cancellationToken = default)
    {
        var ytDlpPath = GetYtDlpPath();

        // 設定保存前でもUIで選択中のチャンネルで更新できるよう、明示指定があればそれを優先する。
        var channel = NormalizeUpdateChannel(
            !string.IsNullOrWhiteSpace(channelOverride) ? channelOverride : _settingsRepository.Load().YtDlpUpdateChannel);

        await _ytDlpUpdateLock.WaitAsync(cancellationToken);
        try
        {
            var result = await RunYtDlpUpdateAsync(ytDlpPath, channel, cancellationToken);
            _hasCheckedYtDlpUpdate = true;
            return result;
        }
        finally
        {
            _ytDlpUpdateLock.Release();
        }
    }

    public async Task<string?> GetYtDlpVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var ytDlpPath = GetYtDlpPath();
            var output = await RunYtDlpAsync(ytDlpPath, new[] { "--version" }, cancellationToken);
            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>更新チャンネル文字列を "stable" / "nightly" のいずれかに正規化する</summary>
    private static string NormalizeUpdateChannel(string? channel)
    {
        return string.Equals(channel?.Trim(), "nightly", StringComparison.OrdinalIgnoreCase)
            ? "nightly"
            : "stable";
    }

    private async Task EnsureYtDlpUpdatedAsync(string ytDlpPath, CancellationToken cancellationToken)
    {
        var settings = _settingsRepository.Load();
        if (!settings.AutoUpdateYtDlp || _hasCheckedYtDlpUpdate)
        {
            return;
        }

        await _ytDlpUpdateLock.WaitAsync(cancellationToken);
        try
        {
            if (_hasCheckedYtDlpUpdate)
            {
                return;
            }

            var channel = NormalizeUpdateChannel(settings.YtDlpUpdateChannel);
            var result = await RunYtDlpUpdateAsync(ytDlpPath, channel, cancellationToken);
            _hasCheckedYtDlpUpdate = true;

            if (!result.IsSuccess)
            {
                // 自動更新失敗は致命的ではないが、後続のダウンロード失敗の伏線になりやすいので記録する
                _logger.Warn($"yt-dlp 自動更新に失敗しました (channel={channel}): {result.Output}");
            }
        }
        finally
        {
            _ytDlpUpdateLock.Release();
        }
    }

    private static async Task<YtDlpUpdateResult> RunYtDlpUpdateAsync(string ytDlpPath, string channel, CancellationToken cancellationToken)
    {
        // --update-to <channel> は stable/nightly 間の切り替え（ダウングレード含む）に対応する。
        // 単なる -U は現在のチャンネル内でしか更新しないため、nightlyへ切り替えるにはこちらを使う。
        var (stdout, stderr, exitCode) = await RunYtDlpRawAsync(
            ytDlpPath,
            new[] { "--update-to", channel },
            cancellationToken);

        var output = string.Join(
            Environment.NewLine,
            new[] { stdout, stderr }.Where(text => !string.IsNullOrWhiteSpace(text)));

        return new YtDlpUpdateResult
        {
            IsSuccess = exitCode == 0,
            Message = exitCode == 0 ? BuildUpdateSuccessMessage(output) : "yt-dlpの更新に失敗しました。",
            Output = output
        };
    }

    private static string BuildUpdateSuccessMessage(string output)
    {
        // 注意: --update-to の出力には常に "Latest version:" 行が含まれるため、
        // 「更新した」判定(Updated/Updating)を先に行う必要がある。
        if (output.Contains("Updated yt-dlp", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Updating to", StringComparison.OrdinalIgnoreCase))
        {
            return "yt-dlpを更新しました。";
        }

        if (output.Contains("is up to date", StringComparison.OrdinalIgnoreCase))
        {
            return "yt-dlpは最新です。";
        }

        return "yt-dlpの更新チェックが完了しました。";
    }

    private static string BuildOutputPath(string saveFolderPath, string template)
    {
        var root = Path.GetFullPath(saveFolderPath);
        var fullPath = Path.GetFullPath(Path.Combine(root, template));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ファイル名テンプレートが保存先フォルダ外を指しています。テンプレートから絶対パスや '..' を削除してください。");
        }

        return fullPath;
    }

    private static string BuildFilenameTemplate(string template, DownloadJob job)
    {
        var result = template
            .Replace("{title}", SanitizeFilename(job.VideoMetadata.Title))
            .Replace("{channel}", SanitizeFilename(job.VideoMetadata.Channel))
            .Replace("{id}", job.VideoMetadata.Id);

        if (job.VideoMetadata.PlaylistIndex.HasValue)
        {
            result = result.Replace("{index}", job.VideoMetadata.PlaylistIndex.Value.ToString("D2"))
                          .Replace("{index:02d}", job.VideoMetadata.PlaylistIndex.Value.ToString("D2"));
        }
        else
        {
            result = result.Replace("{index}", "")
                          .Replace("{index:02d}", "");
        }

        // 拡張子を追加
        // 注意: Path.HasExtension は "Ep.01" の ".01" を拡張子と誤認するため使えない。
        // 既に %(ext)s か既知のメディア拡張子で終わる場合のみ付与を省く。
        result = result.Trim();
        if (!result.EndsWith(".%(ext)s", StringComparison.OrdinalIgnoreCase)
            && !HasKnownMediaExtension(result))
        {
            result += ".%(ext)s";
        }

        return result;
    }

    private static string SanitizeFilename(string filename)
    {
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in invalid)
        {
            filename = filename.Replace(c, '_');
        }
        return filename;
    }

    private static ProgressInfo? ParseProgressLine(string line)
    {
        // [download] 50.0% of 100.00MiB at 5.00MiB/s ETA 00:10
        if (line.Contains("[download]") && line.Contains("%"))
        {
            var progressInfo = new ProgressInfo();

            var percentIndex = line.IndexOf('%');
            if (percentIndex > 0)
            {
                var lastSpaceIndex = line.LastIndexOf(' ', percentIndex - 1);
                var start = lastSpaceIndex >= 0 ? lastSpaceIndex + 1 : line.IndexOf(']') + 1;
                var percentStr = line.Substring(start, percentIndex - start).Trim();
                if (double.TryParse(percentStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                {
                    progressInfo.Percentage = Math.Clamp((int)Math.Floor(percent), 0, 99);
                }
            }

            if (line.Contains(" at "))
            {
                var atIndex = line.IndexOf(" at ");
                var etaIndex = line.IndexOf(" ETA ");
                if (atIndex > 0 && etaIndex > atIndex)
                {
                    progressInfo.Speed = line.Substring(atIndex + 4, etaIndex - atIndex - 4).Trim();
                    progressInfo.Eta = line.Substring(etaIndex + 5).Trim();
                }
            }

            progressInfo.Status = "ダウンロード中";
            return progressInfo;
        }

        if (line.Contains("[download] Destination:"))
        {
            return new ProgressInfo { Status = "開始中...", Percentage = 0 };
        }

        if (line.Contains("[ExtractAudio]") || line.Contains("[ffmpeg]"))
        {
            return new ProgressInfo { Status = "音声変換中...", Percentage = 99, IsPostProcessing = true };
        }

        if (line.Contains("[EmbedThumbnail]"))
        {
            return new ProgressInfo { Status = "サムネイル埋め込み中...", Percentage = 99, IsPostProcessing = true };
        }

        if (line.Contains("[Metadata]"))
        {
            return new ProgressInfo { Status = "メタデータ書き込み中...", Percentage = 99, IsPostProcessing = true };
        }

        if (line.Contains("[Merger]"))
        {
            return new ProgressInfo { Status = "マージ中...", Percentage = 99, IsPostProcessing = true };
        }

        return null;
    }

    private static async Task<string> RunYtDlpAsync(string ytDlpPath, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var (stdout, _, _) = await RunYtDlpRawAsync(ytDlpPath, arguments, cancellationToken);
        return stdout;
    }

    /// <summary>
    /// yt-dlpを実行し、標準出力・標準エラー・終了コードをまとめて返す。
    /// stderrはエラー理由の表示に使うため、ANSIコードページでデコードして文字化けを防ぐ。
    /// </summary>
    private static async Task<(string StdOut, string StdErr, int ExitCode)> RunYtDlpRawAsync(string ytDlpPath, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var psi = CreateYtDlpStartInfo(ytDlpPath, arguments);

        using var process = new Process { StartInfo = psi };
        process.Start();
        using var killRegistration = RegisterProcessKillOnCancellation(process, cancellationToken);

        // stderrも同時に読み取る。リダイレクトしたまま読まないと、
        // 警告などでパイプバッファが満杯になった時点でyt-dlpがブロックしハングする。
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await Task.WhenAll(outputTask, errorTask);
        await process.WaitForExitAsync(cancellationToken);

        return (outputTask.Result, errorTask.Result, process.ExitCode);
    }

    private static ProcessStartInfo CreateYtDlpStartInfo(string ytDlpPath, IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = StdErrEncoding
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        return psi;
    }

    private static CancellationTokenRegistration RegisterProcessKillOnCancellation(Process process, CancellationToken cancellationToken)
    {
        return cancellationToken.Register(() => KillProcessTree(process));
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may already be gone.
        }
    }

    private static string FormatArgumentsForLog(IEnumerable<string> arguments)
    {
        return string.Join(" ", arguments.Select(QuoteArgumentForLog));
    }

    private static string QuoteArgumentForLog(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        return argument.Any(char.IsWhiteSpace) || argument.Contains('"')
            ? $"\"{argument.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""
            : argument;
    }

    /// <summary>
    /// yt-dlpの標準エラー出力から、表示に適した「意味のある」行を抜き出す。
    /// WARNINGなどのノイズを除き、ERROR行を優先して返す。
    /// </summary>
    private static string ExtractMeaningfulError(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return "原因不明のエラーが発生しました。";
        }

        var lines = stderr
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        var errorLines = lines
            .Where(line => line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var chosen = errorLines.Count > 0 ? errorLines : lines.TakeLast(3).ToList();
        return string.Join(Environment.NewLine, chosen);
    }

    /// <summary>ログ行に付けるジョブ識別ラベル（タイトルとURL）を作る</summary>
    private static string BuildJobLabel(DownloadJob job)
    {
        var title = string.IsNullOrWhiteSpace(job.VideoMetadata.Title) ? "(無題)" : job.VideoMetadata.Title;
        var url = string.IsNullOrWhiteSpace(job.VideoMetadata.Url) ? "(URL未設定)" : job.VideoMetadata.Url;
        return $"[{title}] <{url}>";
    }

    /// <summary>
    /// 失敗ログに添えるyt-dlpバージョン。失敗時のみ参照されるため、初回取得結果をキャッシュして
    /// 余計なプロセス起動を避ける。キャンセル済みでも取得できるよう CancellationToken.None で問い合わせる。
    /// </summary>
    private async Task<string> GetYtDlpVersionForLogAsync(string ytDlpPath)
    {
        if (!string.IsNullOrEmpty(_cachedYtDlpVersion))
        {
            return _cachedYtDlpVersion!;
        }

        try
        {
            var version = await RunYtDlpAsync(ytDlpPath, new[] { "--version" }, CancellationToken.None);
            _cachedYtDlpVersion = string.IsNullOrWhiteSpace(version) ? "(不明)" : version.Trim();
        }
        catch
        {
            _cachedYtDlpVersion = "(取得失敗)";
        }
        return _cachedYtDlpVersion!;
    }

    /// <summary>準備段階(URL検証・実行ファイル検出・出力パス構築)の失敗詳細を組み立てる</summary>
    private static string BuildPreflightFailureDetail(DownloadJob job, string phase, string reason, Exception? exception)
    {
        var sb = new StringBuilder();
        AppendFailureHeader(sb, job);
        sb.AppendLine($"失敗フェーズ: {phase}");
        sb.AppendLine($"理由: {reason}");
        if (exception != null)
        {
            sb.AppendLine($"例外: {exception.GetType().FullName}: {exception.Message}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// yt-dlp実行が初回・再試行ともに失敗したときの詳細を組み立てる。
    /// 「どのタイミングで・なぜ」を後から追えるよう、両試行の終了コード・失敗フェーズ・経過時間・
    /// 実行コマンド・stderr全文と、yt-dlpバージョンをまとめる。
    /// </summary>
    private static string BuildDownloadFailureDetail(
        DownloadJob job,
        string ytDlpVersion,
        IReadOnlyList<string> firstArguments,
        IReadOnlyList<string> retryArguments,
        YtDlpRunResult firstRun,
        YtDlpRunResult retryRun)
    {
        var sb = new StringBuilder();
        AppendFailureHeader(sb, job);
        sb.AppendLine($"yt-dlp バージョン: {ytDlpVersion}");
        sb.AppendLine();
        AppendAttemptDetail(sb, "初回試行", firstArguments, firstRun);
        sb.AppendLine();
        AppendAttemptDetail(sb, "再試行(フォールバックclient)", retryArguments, retryRun);
        return sb.ToString().TrimEnd();
    }

    private static void AppendFailureHeader(StringBuilder sb, DownloadJob job)
    {
        sb.AppendLine("==== ダウンロード失敗詳細 ====");
        sb.AppendLine($"日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"タイトル: {job.VideoMetadata.Title}");
        sb.AppendLine($"URL: {job.VideoMetadata.Url}");
        sb.AppendLine($"形式/品質: {job.Format} / {job.Quality}");
        sb.AppendLine($"保存先: {job.SaveFolderPath}");
    }

    private static void AppendAttemptDetail(StringBuilder sb, string label, IReadOnlyList<string> arguments, YtDlpRunResult run)
    {
        sb.AppendLine($"--- {label} ---");
        sb.AppendLine($"失敗フェーズ: {run.LastPhase}");
        sb.AppendLine($"終了コード: {run.ExitCode}");
        sb.AppendLine($"経過時間: {run.Elapsed.TotalSeconds:F1}秒");
        sb.AppendLine($"実行コマンド: {FormatArgumentsForLog(arguments)}");
        sb.AppendLine("stderr:");
        sb.Append(IndentBlock(run.StdErr));
    }

    /// <summary>複数行テキストを2スペースインデントして読みやすくする（空なら「(出力なし)」）</summary>
    private static string IndentBlock(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "  (出力なし)";
        }

        var sb = new StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").TrimEnd().Split('\n'))
        {
            sb.Append("  ").AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }
}
