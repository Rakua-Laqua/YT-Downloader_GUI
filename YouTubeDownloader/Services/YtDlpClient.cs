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
    Task<YtDlpUpdateResult> UpdateYtDlpAsync(CancellationToken cancellationToken = default);
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
}

public class YtDlpClient : IYtDlpClient
{
    private readonly ISettingsRepository _settingsRepository;
    private static string? _cachedYtDlpPath;
    private static string? _cachedFfmpegPath;
    private readonly SemaphoreSlim _ytDlpUpdateLock = new(1, 1);
    private bool _hasCheckedYtDlpUpdate;

    public YtDlpClient(ISettingsRepository settingsRepository)
    {
        _settingsRepository = settingsRepository;
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
        
        // 自動検出
        _cachedYtDlpPath = FindExecutable("yt-dlp.exe", "yt-dlp");
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
        _cachedFfmpegPath = FindExecutable("ffmpeg.exe", "ffmpeg");
        return _cachedFfmpegPath;
    }

    private static string? FindExecutable(string windowsName, string unixName)
    {
        var exeName = Environment.OSVersion.Platform == PlatformID.Win32NT ? windowsName : unixName;
        
        // 1. PATH環境変数から検索
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var path in pathEnv.Split(Path.PathSeparator))
            {
                var fullPath = Path.Combine(path, exeName);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }
        
        // 2. 一般的なインストール場所を検索 (Windows)
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            var commonPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", exeName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), exeName.Replace(".exe", ""), exeName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), exeName.Replace(".exe", ""), exeName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", exeName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Programs", exeName.Replace(".exe", ""), exeName),
                Path.Combine("C:\\", exeName.Replace(".exe", ""), exeName),
                Path.Combine("C:\\tools", exeName),
            };
            
            foreach (var path in commonPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }
        
        // 3. アプリケーションディレクトリ
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var appPath = Path.Combine(appDir, exeName);
        if (File.Exists(appPath))
        {
            return appPath;
        }
        
        return null;
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
                var playlistInfoResult = await RunYtDlpAsync(ytDlpPath, $"{metadataLanguageArgs}--dump-single-json --flat-playlist \"{url}\"", cancellationToken);
                if (!string.IsNullOrEmpty(playlistInfoResult))
                {
                    return ParsePlaylistFromJson(playlistInfoResult);
                }
            }

            // 単一動画として解析（1回の呼び出しのみ）
            var videoResult = await RunYtDlpAsync(ytDlpPath, $"{metadataLanguageArgs}--dump-json --no-playlist \"{url}\"", cancellationToken);
            if (!string.IsNullOrEmpty(videoResult))
            {
                var video = ParseVideoMetadata(videoResult, url);
                if (video != null)
                {
                    return new YtDlpAnalyzeResult
                    {
                        IsSuccess = true,
                        IsPlaylist = false,
                        VideoMetadata = video
                    };
                }
            }

            return new YtDlpAnalyzeResult
            {
                IsSuccess = false,
                ErrorMessage = "動画情報を取得できませんでした。URLが正しいか確認してください。"
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

    private YtDlpAnalyzeResult ParsePlaylistFromJson(string json)
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

    private async Task<YtDlpAnalyzeResult> ParsePlaylistAsync(string ytDlpPath, string url, string[] lines, CancellationToken cancellationToken)
    {
        var playlist = new PlaylistMetadata();
        var videos = new List<VideoMetadata>();

        var localizationArgs = BuildLocalizationArgs();

        // プレイリスト情報を取得
        var playlistInfoResult = await RunYtDlpAsync(ytDlpPath, $"{localizationArgs} --dump-single-json --flat-playlist \"{url}\"", cancellationToken);
        if (!string.IsNullOrEmpty(playlistInfoResult))
        {
            try
            {
                using var doc = JsonDocument.Parse(playlistInfoResult);
                var root = doc.RootElement;

                playlist.Id = GetStringProperty(root, "id") ?? "";
                playlist.Title = GetStringProperty(root, "title") ?? "プレイリスト";
                playlist.Channel = GetStringProperty(root, "uploader") ?? GetStringProperty(root, "channel") ?? "";
                playlist.ThumbnailUrl = GetThumbnailUrl(root);

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

                return new YtDlpAnalyzeResult
                {
                    IsSuccess = true,
                    IsPlaylist = true,
                    PlaylistMetadata = playlist
                };
            }
            catch
            {
                // パースに失敗した場合は単一動画として再解析
            }
        }

        return new YtDlpAnalyzeResult
        {
            IsSuccess = false,
            ErrorMessage = "プレイリスト情報の取得に失敗しました。"
        };
    }

    private VideoMetadata? ParseVideoMetadata(string json, string url)
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

    private string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private int GetIntProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number)
            {
                return prop.GetInt32();
            }
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var result))
            {
                return result;
            }
        }
        return 0;
    }

    private string GetThumbnailUrl(JsonElement element)
    {
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

    private static string BuildMetadataLanguageArguments(string? language)
    {
        var normalized = NormalizeMetadataLanguage(language);
        return string.IsNullOrEmpty(normalized)
            ? string.Empty
            : $"--extractor-args \"youtube:lang={normalized}\" ";
    }

    private static string NormalizeMetadataLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "ja";
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
                return "ja";
            }
        }

        return trimmed;
    }

    public async Task DownloadAsync(DownloadJob job, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken = default)
    {
        // URLが空の場合はエラー
        if (string.IsNullOrEmpty(job.VideoMetadata.Url))
        {
            throw new Exception("動画URLが指定されていません");
        }

        var ytDlpPath = GetYtDlpPath();
        await EnsureYtDlpUpdatedAsync(ytDlpPath, cancellationToken);
        var settings = _settingsRepository.Load();

        // 保存先フォルダを作成
        Directory.CreateDirectory(job.SaveFolderPath);

        // ファイル名テンプレートを構築
        var template = BuildFilenameTemplate(settings.FilenameTemplate, job);
        var outputPath = Path.Combine(job.SaveFolderPath, template);

        var requestedFormat = NormalizeFormat(job.Format);

        // コマンド引数を構築
        var args = new StringBuilder();

        // タイトル等のローカライズを日本語優先にする（YouTube側に日本語タイトルが存在する場合に反映される）
        // 403 リトライ時に置換できるよう、文字列として保持しておく
        var youtubeExtractorArgs = BuildYoutubeExtractorArgs();
        args.Append($"{youtubeExtractorArgs} ");
        args.Append($"--add-header \"{AcceptLanguageHeaderJaJp}\" ");

        // 出力テンプレート
        args.Append($"-o \"{outputPath}\" ");
        args.Append("--force-overwrites ");
        args.Append(BuildMetadataLanguageArguments(settings.DefaultMetadataLanguage));

        // フォーマット指定
        if (IsAudioFormat(requestedFormat))
        {
            // 音声のみ - bestaudioを取得して変換
            args.Append("-f \"bestaudio/best\" ");
            args.Append("-x ");
            args.Append($"--audio-format {requestedFormat} ");
            args.Append("--audio-quality 0 ");
        }
        else
        {
            args.Append($"-f \"{BuildVideoFormatSelector(job.Quality, requestedFormat)}\" ");
            args.Append($"--merge-output-format {requestedFormat} ");
        }

        // メタデータを埋め込む
        args.Append("--embed-metadata ");
        
        // サムネイルを埋め込む（音声ファイルの場合はアートワークとして）
        if (requestedFormat == "mp3" || requestedFormat == "m4a")
        {
            args.Append("--embed-thumbnail ");
        }

        // ffmpegパスを自動検出または設定から取得
        var ffmpegPath = !string.IsNullOrEmpty(settings.FfmpegPath) && File.Exists(settings.FfmpegPath) 
            ? settings.FfmpegPath 
            : GetFfmpegPath();
        
        if (!string.IsNullOrEmpty(ffmpegPath))
        {
            var ffmpegDir = Path.GetDirectoryName(ffmpegPath);
            args.Append($"--ffmpeg-location \"{ffmpegDir}\" ");
        }

        // YouTube対策: User-Agent は指定するが、player_client=web の強制は
        // SABR でURLが欠落し「Requested format is not available」になり得るため行わない。
        args.Append("--user-agent \"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36\" ");

        // 進捗表示用
        args.Append("--newline ");
        args.Append("--no-warnings ");

        // URL
        args.Append($"\"{job.VideoMetadata.Url}\"");

        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            Arguments = args.ToString(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        System.Diagnostics.Debug.WriteLine($"yt-dlp: {ytDlpPath} {args}");

        using var process = new Process { StartInfo = psi };
        process.Start();

        // 出力を読み取りながら進捗を報告
        var outputTask = Task.Run(async () =>
        {
            while (!process.StandardOutput.EndOfStream)
            {
                var line = await process.StandardOutput.ReadLineAsync();
                if (line != null && progress != null)
                {
                    var progressInfo = ParseProgressLine(line);
                    if (progressInfo != null)
                    {
                        progress.Report(progressInfo);
                    }
                }
            }
        }, cancellationToken);

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
        }, cancellationToken);

        await Task.WhenAll(outputTask, errorTask);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            var stderr = errorOutput.ToString();

            // 403系は android client で再試行（web強制より成功率が高い）
            if (stderr.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("403", StringComparison.OrdinalIgnoreCase))
            {
                var retryArgs = new StringBuilder(args.ToString());
                retryArgs.Append(" --extractor-args \"youtube:player_client=android\"");

                var retryPsi = new ProcessStartInfo
                {
                    FileName = ytDlpPath,
                    Arguments = retryArgs.ToString(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                System.Diagnostics.Debug.WriteLine($"yt-dlp retry(android): {ytDlpPath} {retryArgs}");

                using var retryProcess = new Process { StartInfo = retryPsi };
                retryProcess.Start();

                var retryOutputTask = Task.Run(async () =>
                {
                    while (!retryProcess.StandardOutput.EndOfStream)
                    {
                        var line = await retryProcess.StandardOutput.ReadLineAsync();
                        if (line != null && progress != null)
                        {
                            var progressInfo = ParseProgressLine(line);
                            if (progressInfo != null)
                            {
                                progress.Report(progressInfo);
                            }
                        }
                    }
                }, cancellationToken);

                var retryErrorOutput = new StringBuilder();
                var retryErrorTask = Task.Run(async () =>
                {
                    while (!retryProcess.StandardError.EndOfStream)
                    {
                        var line = await retryProcess.StandardError.ReadLineAsync();
                        if (line != null)
                        {
                            retryErrorOutput.AppendLine(line);
                        }
                    }
                }, cancellationToken);

                await Task.WhenAll(retryOutputTask, retryErrorTask);
                await retryProcess.WaitForExitAsync(cancellationToken);

                if (retryProcess.ExitCode != 0)
                {
                    throw new Exception($"ダウンロードに失敗しました: {retryErrorOutput}");
                }
            }
            else if (IsHttpForbidden(stderr))
            {
                throw new Exception(
                    "高画質形式のダウンロードが YouTube 側の 403 エラーで拒否されました。" +
                    "360pへ自動低下しないよう停止しました。yt-dlp を最新版へ更新するか、" +
                    "必要に応じて cookies / PO Token を設定してから再試行してください。");
            }
            else
            {
                throw new Exception($"ダウンロードに失敗しました: {stderr}");
            }
        }

        // ダウンロードしたファイルのパスを更新
        var outputBaseName = template.EndsWith(".%(ext)s", StringComparison.OrdinalIgnoreCase)
            ? template[..^".%(ext)s".Length]
            : Path.GetFileNameWithoutExtension(template);
        var files = Directory.GetFiles(job.SaveFolderPath, $"{Path.GetFileName(outputBaseName)}.*");
        if (files.Length > 0)
        {
            job.VideoMetadata.LocalFilePath = files[0];
        }
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

    private static string BuildVideoFormatSelector(string quality, string requestedFormat)
    {
        return requestedFormat == "mp4"
            ? BuildMp4VideoFormatSelector(quality)
            : BuildGeneralVideoFormatSelector(quality);
    }

    private static string BuildMp4VideoFormatSelector(string quality)
    {
        return quality switch
        {
            "1080p" => BuildMp4SelectorForHeights(1080, 720),
            "720p" => BuildMp4SelectorForHeights(720, 480),
            "480p" => BuildMp4SelectorForHeights(480, 360),
            "360p" => BuildMp4SelectorForHeights(360, 360),
            _ => "bv*[ext=mp4][vcodec^=avc1]+ba[ext=m4a]/bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/bv*+ba/b"
        };
    }

    private static string BuildMp4SelectorForHeights(int targetHeight, int fallbackMinHeight)
    {
        var exactHeightSelector =
            $"bv*[height={targetHeight}][ext=mp4][vcodec^=avc1]+ba[ext=m4a]/" +
            $"bv*[height={targetHeight}][ext=mp4]+ba[ext=m4a]/" +
            $"b[height={targetHeight}][ext=mp4]";

        if (fallbackMinHeight >= targetHeight)
        {
            return exactHeightSelector;
        }

        var fallbackSelector =
            $"bv*[height<={targetHeight}][height>={fallbackMinHeight}][ext=mp4][vcodec^=avc1]+ba[ext=m4a]/" +
            $"bv*[height<={targetHeight}][height>={fallbackMinHeight}][ext=mp4]+ba[ext=m4a]/" +
            $"b[height<={targetHeight}][height>={fallbackMinHeight}][ext=mp4]";

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

    private static bool IsHttpForbidden(string stderr)
    {
        return stderr.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase) ||
               stderr.Contains("403: Forbidden", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanRetryWithAndroidWithoutQualityDowngrade(DownloadJob job, string requestedFormat)
    {
        return IsAudioFormat(requestedFormat) || job.Quality == "360p";
    }

    public async Task<YtDlpUpdateResult> UpdateYtDlpAsync(CancellationToken cancellationToken = default)
    {
        var ytDlpPath = GetYtDlpPath();

        await _ytDlpUpdateLock.WaitAsync(cancellationToken);
        try
        {
            var result = await RunYtDlpUpdateAsync(ytDlpPath, cancellationToken);
            _hasCheckedYtDlpUpdate = true;
            return result;
        }
        finally
        {
            _ytDlpUpdateLock.Release();
        }
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

            var result = await RunYtDlpUpdateAsync(ytDlpPath, cancellationToken);
            _hasCheckedYtDlpUpdate = true;

            if (!result.IsSuccess)
            {
                Debug.WriteLine($"yt-dlp auto update failed: {result.Output}");
            }
        }
        finally
        {
            _ytDlpUpdateLock.Release();
        }
    }

    private static async Task<YtDlpUpdateResult> RunYtDlpUpdateAsync(string ytDlpPath, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            Arguments = "-U",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await Task.WhenAll(outputTask, errorTask);
        await process.WaitForExitAsync(cancellationToken);

        var output = string.Join(
            Environment.NewLine,
            new[] { outputTask.Result, errorTask.Result }.Where(text => !string.IsNullOrWhiteSpace(text)));

        return new YtDlpUpdateResult
        {
            IsSuccess = process.ExitCode == 0,
            Message = process.ExitCode == 0 ? BuildUpdateSuccessMessage(output) : "yt-dlpの更新に失敗しました。",
            Output = output
        };
    }

    private static string BuildUpdateSuccessMessage(string output)
    {
        if (output.Contains("is up to date", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Latest version", StringComparison.OrdinalIgnoreCase))
        {
            return "yt-dlpは最新です。";
        }

        if (output.Contains("Updated", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Updating", StringComparison.OrdinalIgnoreCase))
        {
            return "yt-dlpを更新しました。";
        }

        return "yt-dlpの更新チェックが完了しました。";
    }

    private string BuildFilenameTemplate(string template, DownloadJob job)
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
        result = result.Trim();
        if (!Path.HasExtension(result))
        {
            result += $".%(ext)s";
        }

        return result;
    }

    private string SanitizeFilename(string filename)
    {
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in invalid)
        {
            filename = filename.Replace(c, '_');
        }
        return filename;
    }

    private ProgressInfo? ParseProgressLine(string line)
    {
        // [download] 50.0% of 100.00MiB at 5.00MiB/s ETA 00:10
        if (line.Contains("[download]") && line.Contains("%"))
        {
            var progressInfo = new ProgressInfo();

            var percentIndex = line.IndexOf('%');
            if (percentIndex > 0)
            {
                var start = line.LastIndexOf(' ', percentIndex - 1) + 1;
                if (start < 0) start = line.IndexOf(']') + 1;
                var percentStr = line.Substring(start, percentIndex - start).Trim();
                if (double.TryParse(percentStr, out var percent))
                {
                    progressInfo.Percentage = (int)percent;
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

        if (line.Contains("[Merger]") || line.Contains("[ffmpeg]"))
        {
            return new ProgressInfo { Status = "変換中...", Percentage = 99 };
        }

        return null;
    }

    private async Task<string> RunYtDlpAsync(string ytDlpPath, string arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return output;
    }

    private static string BuildLocalizationArgs()
        => $"{BuildYoutubeExtractorArgs()} --add-header \"{AcceptLanguageHeaderJaJp}\"";

    private static string BuildYoutubeExtractorArgs(string? playerClient = null)
    {
        var sb = new StringBuilder("--extractor-args \"youtube:lang=ja");
        if (!string.IsNullOrWhiteSpace(playerClient))
        {
            sb.Append($";player_client={playerClient}");
        }
        sb.Append("\"");
        return sb.ToString();
    }
}
