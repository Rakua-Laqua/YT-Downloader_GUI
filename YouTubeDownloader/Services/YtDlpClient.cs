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
    Task DownloadAsync(DownloadJob job, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken = default);
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

    public YtDlpClient(ISettingsRepository settingsRepository)
    {
        _settingsRepository = settingsRepository;
    }

    private string GetYtDlpPath()
    {
        var settings = _settingsRepository.Load();
        if (string.IsNullOrEmpty(settings.YtDlpPath) || !File.Exists(settings.YtDlpPath))
        {
            throw new InvalidOperationException("yt-dlpの実行ファイルパスが設定されていません。設定画面で指定してください。");
        }
        return settings.YtDlpPath;
    }

    public async Task<YtDlpAnalyzeResult> AnalyzeUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            var ytDlpPath = GetYtDlpPath();

            // URLの種類を判定してから適切な方法で解析
            bool isPlaylistUrl = url.Contains("list=") || url.Contains("/playlist");

            if (isPlaylistUrl)
            {
                // プレイリストURL: --flat-playlistで高速に一覧取得
                var playlistInfoResult = await RunYtDlpAsync(ytDlpPath, $"--dump-single-json --flat-playlist \"{url}\"", cancellationToken);
                if (!string.IsNullOrEmpty(playlistInfoResult))
                {
                    return ParsePlaylistFromJson(playlistInfoResult);
                }
            }

            // 単一動画として解析（1回の呼び出しのみ）
            var videoResult = await RunYtDlpAsync(ytDlpPath, $"--dump-json --no-playlist \"{url}\"", cancellationToken);
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
                    var video = new VideoMetadata
                    {
                        Id = GetStringProperty(entry, "id") ?? "",
                        Title = GetStringProperty(entry, "title") ?? $"動画 {index}",
                        Channel = GetStringProperty(entry, "uploader") ?? GetStringProperty(entry, "channel") ?? playlist.Channel,
                        DurationSeconds = GetIntProperty(entry, "duration"),
                        ThumbnailUrl = GetThumbnailUrl(entry),
                        Url = GetStringProperty(entry, "url") ?? GetStringProperty(entry, "webpage_url") ?? $"https://www.youtube.com/watch?v={GetStringProperty(entry, "id")}",
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

        // プレイリスト情報を取得
        var playlistInfoResult = await RunYtDlpAsync(ytDlpPath, $"--dump-single-json --flat-playlist \"{url}\"", cancellationToken);
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
                        var video = new VideoMetadata
                        {
                            Id = GetStringProperty(entry, "id") ?? "",
                            Title = GetStringProperty(entry, "title") ?? $"動画 {index}",
                            Channel = GetStringProperty(entry, "uploader") ?? GetStringProperty(entry, "channel") ?? playlist.Channel,
                            DurationSeconds = GetIntProperty(entry, "duration"),
                            ThumbnailUrl = GetThumbnailUrl(entry),
                            Url = GetStringProperty(entry, "url") ?? GetStringProperty(entry, "webpage_url") ?? $"https://www.youtube.com/watch?v={GetStringProperty(entry, "id")}",
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

    public async Task DownloadAsync(DownloadJob job, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken = default)
    {
        var ytDlpPath = GetYtDlpPath();
        var settings = _settingsRepository.Load();

        // 保存先フォルダを作成
        Directory.CreateDirectory(job.SaveFolderPath);

        // ファイル名テンプレートを構築
        var template = BuildFilenameTemplate(settings.FilenameTemplate, job);
        var outputPath = Path.Combine(job.SaveFolderPath, template);

        // コマンド引数を構築
        var args = new StringBuilder();

        // 出力テンプレート
        args.Append($"-o \"{outputPath}\" ");

        // フォーマット指定
        if (job.Format == "mp3" || job.Format == "m4a" || job.Format == "wav")
        {
            // 音声のみ
            args.Append("-x ");
            args.Append($"--audio-format {job.Format} ");
            args.Append("--audio-quality 0 ");
        }
        else
        {
            // 動画
            if (job.Quality == "best")
            {
                args.Append("-f \"bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best\" ");
            }
            else if (job.Quality == "1080p")
            {
                args.Append("-f \"bestvideo[height<=1080][ext=mp4]+bestaudio[ext=m4a]/best[height<=1080][ext=mp4]/best\" ");
            }
            else if (job.Quality == "720p")
            {
                args.Append("-f \"bestvideo[height<=720][ext=mp4]+bestaudio[ext=m4a]/best[height<=720][ext=mp4]/best\" ");
            }
            else
            {
                args.Append("-f \"bestvideo+bestaudio/best\" ");
            }
            args.Append("--merge-output-format mp4 ");
        }

        // メタデータを埋め込む
        args.Append("--embed-metadata ");
        
        // サムネイルを埋め込む（音声ファイルの場合はアートワークとして）
        if (job.Format == "mp3" || job.Format == "m4a")
        {
            args.Append("--embed-thumbnail ");
        }

        // ffmpegパスが設定されていれば指定
        if (!string.IsNullOrEmpty(settings.FfmpegPath) && File.Exists(settings.FfmpegPath))
        {
            var ffmpegDir = Path.GetDirectoryName(settings.FfmpegPath);
            args.Append($"--ffmpeg-location \"{ffmpegDir}\" ");
        }

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
            StandardOutputEncoding = Encoding.UTF8
        };

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
            throw new Exception($"ダウンロードに失敗しました: {errorOutput}");
        }

        // ダウンロードしたファイルのパスを更新
        var files = Directory.GetFiles(job.SaveFolderPath, $"{Path.GetFileNameWithoutExtension(template)}.*");
        if (files.Length > 0)
        {
            job.VideoMetadata.LocalFilePath = files[0];
        }
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
        var ext = job.Format == "mp3" || job.Format == "m4a" ? job.Format : "mp4";
        result = result.Trim();
        if (!result.EndsWith($".{ext}"))
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
}
