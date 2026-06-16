using System;
using System.Globalization;
using System.IO;
using System.Linq;
using YouTubeDownloader.Models;

namespace YouTubeDownloader.Services;

internal static class YtDlpOutputPathResolver
{
    public static string BuildOutputPath(string saveFolderPath, string template)
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

    public static string BuildFilenameTemplate(string template, DownloadJob job)
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

    /// <summary>
    /// yt-dlpが書き出した公開時刻の一時ファイル（"timestamp|upload_date" 形式）を読み取る。
    /// timestamp(UNIX秒)があれば分・秒まで含むUTC絶対時刻(Kind=Utc)を返す。
    /// 無ければ upload_date(YYYYMMDD) のローカル0時(Kind=Unspecified)を返す。
    /// 再試行で複数行になり得るため、最初に解釈できた行を採用する。
    /// </summary>
    public static YtDlpDownloadInfoResult ReadDownloadInfoFile(string path)
    {
        var result = new YtDlpDownloadInfoResult();
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
    /// ダウンロード完了後、出力テンプレートに一致する実ファイルを探してジョブに記録する
    /// </summary>
    public static void UpdateLocalFilePath(DownloadJob job, string outputPath, string requestedFormat)
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

    private static string SanitizeFilename(string filename)
    {
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in invalid)
        {
            filename = filename.Replace(c, '_');
        }
        return filename;
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
}
