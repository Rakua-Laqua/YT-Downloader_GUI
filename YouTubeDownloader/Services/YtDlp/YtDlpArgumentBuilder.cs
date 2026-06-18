using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using YouTubeDownloader.Models;

namespace YouTubeDownloader.Services;

internal static class YtDlpArgumentBuilder
{
    /// <summary>
    /// UNPLAYABLE/403などでデフォルトのclientが失敗した場合に再試行で使うplayer client群。
    /// </summary>
    public const string FallbackPlayerClients = "tv,web_safari,android";

    public static List<string> BuildMetadataLanguageArguments(string? language, string? playerClient = null)
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

    /// <summary>
    /// ダウンロード用のyt-dlpコマンドライン引数を構築する
    /// </summary>
    public static List<string> BuildDownloadArguments(
        DownloadJob job,
        AppSettings settings,
        string outputPath,
        string requestedFormat,
        string? ffmpegPath,
        string? conversionProgressFile = null,
        string? downloadInfoFile = null,
        string? sourceFormatFile = null)
    {
        var args = new List<string>();

        // タイトル等のローカライズは設定画面の「タイトル取得言語」を優先する
        // 出力テンプレート
        args.Add("-o");
        args.Add(outputPath);
        args.Add("--no-overwrites");
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

        // yt-dlpが実際に選択したソースストリームの情報を一時ファイルへ書き出す。
        // video: フェーズはフォーマット選択完了後・ダウンロード前に発火する。
        // %(vcodec)s / %(acodec)s はYouTube側が公開しているソースストリームのコーデックを返すため、
        // ダウンロード後の実ファイル(ffprobe)と突き合わせて整合性を検証できる。
        // 備考: %(ext)s は「出力コンテナ」(マージ後の拡張子)を返すため、必ずしもソースコンテナではない。
        //       コンテナ比較は参考情報程度に扱う。
        if (!string.IsNullOrEmpty(sourceFormatFile))
        {
            args.Add("--print-to-file");
            args.Add("video:%(ext)s|%(vcodec)s|%(acodec)s");
            args.Add(sourceFormatFile);
        }

        // サムネイルを埋め込む（音声ファイルの場合はアートワークとして）
        if (requestedFormat == "mp3" || requestedFormat == "m4a")
        {
            args.Add("--embed-thumbnail");
        }

        if (!string.IsNullOrEmpty(ffmpegPath))
        {
            var ffmpegDir = Path.GetDirectoryName(ffmpegPath);
            if (!string.IsNullOrEmpty(ffmpegDir))
            {
                args.Add("--ffmpeg-location");
                args.Add(ffmpegDir);
            }
        }

        // User-Agent は yt-dlp 側の既定値に任せ、更新に追従する。

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
    public static List<string> BuildFallbackClientArguments(IReadOnlyList<string> arguments, AppSettings settings)
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

    public static string NormalizeFormat(string format)
    {
        var normalized = format.Trim().ToLowerInvariant();
        return IsAudioFormat(normalized) || normalized is "mp4" or "mkv" or "webm"
            ? normalized
            : "mp4";
    }

    public static bool IsAudioFormat(string format)
    {
        return format is "mp3" or "m4a" or "wav";
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
}
