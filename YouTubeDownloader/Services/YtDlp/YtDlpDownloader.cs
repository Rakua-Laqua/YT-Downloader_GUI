using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YouTubeDownloader.Models;

namespace YouTubeDownloader.Services;

internal sealed class YtDlpDownloader
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly ILoggingService _logger;
    private readonly YtDlpUpdater _updater;
    private readonly Func<string> _getYtDlpPath;
    private readonly Func<string?> _getFfmpegPath;
    private string? _cachedYtDlpVersion;

    public YtDlpDownloader(
        ISettingsRepository settingsRepository,
        ILoggingService logger,
        YtDlpUpdater updater,
        Func<string> getYtDlpPath,
        Func<string?> getFfmpegPath)
    {
        _settingsRepository = settingsRepository;
        _logger = logger;
        _updater = updater;
        _getYtDlpPath = getYtDlpPath;
        _getFfmpegPath = getFfmpegPath;
    }

    public async Task DownloadAsync(DownloadJob job, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken = default)
    {
        var jobLabel = YtDlpFailureFormatter.BuildJobLabel(job);
        _logger.Info($"ダウンロード開始 {jobLabel} / 形式={job.Format} 品質={job.Quality} 保存先=\"{job.SaveFolderPath}\"");

        // URLが空の場合はエラー
        if (string.IsNullOrEmpty(job.VideoMetadata.Url))
        {
            const string msg = "動画URLが指定されていません";
            job.FailureDetail = YtDlpFailureFormatter.BuildPreflightFailureDetail(job, "準備(URL検証)", msg, null);
            _logger.Error($"ダウンロード失敗 {jobLabel} / フェーズ=準備(URL検証): {msg}");
            throw new Exception(msg);
        }

        // 準備段階(実行ファイル検出・保存先作成・出力パス構築)は失敗フェーズが分かるよう個別に記録する
        string ytDlpPath;
        try
        {
            ytDlpPath = _getYtDlpPath();
        }
        catch (Exception ex)
        {
            job.FailureDetail = YtDlpFailureFormatter.BuildPreflightFailureDetail(job, "準備(yt-dlp検出)", ex.Message, ex);
            _logger.Error($"ダウンロード失敗 {jobLabel} / フェーズ=準備(yt-dlp検出)", ex);
            throw;
        }

        await _updater.EnsureYtDlpUpdatedAsync(ytDlpPath, cancellationToken);
        var settings = _settingsRepository.Load();

        string outputPath;
        string requestedFormat;
        try
        {
            // 保存先フォルダを作成
            Directory.CreateDirectory(job.SaveFolderPath);

            // ファイル名テンプレートを構築
            var template = YtDlpOutputPathResolver.BuildFilenameTemplate(settings.FilenameTemplate, job);
            outputPath = YtDlpOutputPathResolver.BuildOutputPath(job.SaveFolderPath, template);

            requestedFormat = YtDlpArgumentBuilder.NormalizeFormat(job.Format);
        }
        catch (Exception ex)
        {
            job.FailureDetail = YtDlpFailureFormatter.BuildPreflightFailureDetail(job, "準備(保存先/出力パス構築)", ex.Message, ex);
            _logger.Error($"ダウンロード失敗 {jobLabel} / フェーズ=準備(保存先/出力パス構築)", ex);
            throw;
        }

        // mp3 / wav は再エンコード(transcode)が走る。ffmpegに変換進捗をファイル出力させ、
        // 動画長と突き合わせて「音声変換中」の実進捗を出すための一時ファイルを用意する。
        // (m4aはコピーで一瞬／動画長が不明な場合は実進捗を出せないので対象外)
        var durationSeconds = job.VideoMetadata.DurationSeconds;
        string? conversionProgressFile = YtDlpArgumentBuilder.IsAudioFormat(requestedFormat) && requestedFormat != "m4a" && durationSeconds > 0
            ? Path.Combine(Path.GetTempPath(), $"ytdlp_ffprog_{job.Id:N}.txt")
            : null;

        // ダウンロードした正確なファイルパスと公開日時を書き出させる一時ファイル
        string downloadInfoFile = Path.Combine(Path.GetTempPath(), $"ytdlp_info_{job.Id:N}.txt");

        // yt-dlpが選択したソースストリーム(ext/vcodec/acodec)を書き出させる一時ファイル
        string sourceFormatFile = Path.Combine(Path.GetTempPath(), $"ytdlp_srcfmt_{job.Id:N}.txt");

        var ffmpegPath = ResolveFfmpegPath(settings);
        var arguments = YtDlpArgumentBuilder.BuildDownloadArguments(
            job,
            settings,
            outputPath,
            requestedFormat,
            ffmpegPath,
            conversionProgressFile,
            downloadInfoFile,
            sourceFormatFile);

        var processStartedAtUtc = DateTime.UtcNow;
        var firstRunFailed = false;
        try
        {
            _logger.Info($"yt-dlp 実行 {jobLabel}: {ytDlpPath} {YtDlpFailureFormatter.FormatArgumentsForLog(arguments)}");
            var firstRun = await YtDlpDownloadRunner.RunDownloadProcessAsync(ytDlpPath, arguments, progress, conversionProgressFile, durationSeconds, cancellationToken);
            LogRunSummary(jobLabel, "yt-dlp 初回出力要約", firstRun);

            if (firstRun.ExitCode != 0)
            {
                firstRunFailed = true;

                // bot検知・年齢制限・非公開・メンバー限定・429・地域制限・著作権・URL不正など、別clientで
                // 再試行しても回復しない既知エラーは即座に失敗として扱う。特に bot/429 は無駄な再アクセスが
                // IP制限（bot判定）を悪化させるため、再試行しないことが取得成功率の維持にもつながる。
                if (!YtDlpFailureFormatter.ShouldRetryWithFallbackClient(firstRun))
                {
                    var ytDlpVersionNoRetry = await GetYtDlpVersionForLogAsync(ytDlpPath);
                    var singleDetail = YtDlpFailureFormatter.BuildDownloadFailureDetail(job, ytDlpVersionNoRetry, arguments, firstRun);
                    job.FailureDetail = singleDetail;
                    _logger.Error($"ダウンロード失敗（別clientでも回復不能と判断し再試行せず） {jobLabel}{Environment.NewLine}{singleDetail}");
                    throw new Exception($"ダウンロードに失敗しました:\n{YtDlpFailureFormatter.DescribeError(firstRun)}");
                }

                _logger.Warn($"yt-dlp 初回失敗 {jobLabel} / 終了コード={firstRun.ExitCode} 失敗フェーズ={firstRun.LastPhase} 経過={firstRun.Elapsed.TotalSeconds:F1}秒。フォールバックclientで再試行します。");

                // 初回(tv等)が回復可能な理由で失敗した場合は、別のplayer client群で1回だけ再試行する。
                var retryArguments = YtDlpArgumentBuilder.BuildFallbackClientArguments(arguments, settings);

                _logger.Info($"yt-dlp 再試行 {jobLabel}: {ytDlpPath} {YtDlpFailureFormatter.FormatArgumentsForLog(retryArguments)}");
                var retryRun = await YtDlpDownloadRunner.RunDownloadProcessAsync(ytDlpPath, retryArguments, progress, conversionProgressFile, durationSeconds, cancellationToken);
                LogRunSummary(jobLabel, "yt-dlp 再試行出力要約", retryRun);

                if (retryRun.ExitCode != 0)
                {
                    // 失敗の「どのタイミングで・なぜ」が後から追えるよう、両試行の終了コード・失敗フェーズ・
                    // 経過時間・実行コマンド・stderr全文・yt-dlpバージョンをまとめて記録する。
                    var ytDlpVersion = await GetYtDlpVersionForLogAsync(ytDlpPath);
                    var detail = YtDlpFailureFormatter.BuildDownloadFailureDetail(job, ytDlpVersion, arguments, retryArguments, firstRun, retryRun);
                    job.FailureDetail = detail;
                    _logger.Error($"ダウンロード失敗 {jobLabel}{Environment.NewLine}{detail}");

                    // 既知のエラーパターンは正確な原因・対処に翻訳して利用者に提示する
                    var combinedReason = YtDlpFailureFormatter.BuildUserFacingDownloadReason(firstRun, retryRun);
                    throw new Exception($"ダウンロードに失敗しました:\n{combinedReason}");
                }

                _logger.Info($"yt-dlp 再試行成功 {jobLabel} / 経過={retryRun.Elapsed.TotalSeconds:F1}秒");
                _logger.Warn(
                    $"yt-dlp 初回失敗診断 {jobLabel}{Environment.NewLine}" +
                    YtDlpFailureFormatter.BuildAttemptDiagnosticDetail("初回試行", arguments, firstRun));
            }

            var infoResult = YtDlpOutputPathResolver.ReadDownloadInfoFile(downloadInfoFile);

            // 1) yt-dlpが書き出した正確なファイルパスがある場合、それを最優先で使用する
            if (!string.IsNullOrEmpty(infoResult.FilePath) && File.Exists(infoResult.FilePath))
            {
                job.VideoMetadata.LocalFilePath = infoResult.FilePath;
            }
            else
            {
                // 2) 取得できなかった場合のフォールバックとして、従来の走査（推測）を行う
                YtDlpOutputPathResolver.UpdateLocalFilePath(job, outputPath, requestedFormat);
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

            // ソースフォーマット(yt-dlp選択)と実ファイル(ffprobe)を突き合わせて検証・ログ出力する
            var sourceFmt = ReadSourceFormat(sourceFormatFile);
            job.SourceExt = sourceFmt?.Ext;
            job.SourceVcodec = sourceFmt?.Vcodec;
            job.SourceAcodec = sourceFmt?.Acodec;

            var actualFmt = await ProbeFileFormatAsync(job.VideoMetadata.LocalFilePath, ffmpegPath);
            job.ActualExt = actualFmt?.Ext;
            job.ActualVcodec = actualFmt?.Vcodec;
            job.ActualAcodec = actualFmt?.Acodec;

            CompareAndLogFormats(job);

            CleanupFallbackLeftovers(jobLabel, outputPath, requestedFormat, firstRunFailed, processStartedAtUtc);
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

            try
            {
                if (File.Exists(sourceFormatFile))
                {
                    File.Delete(sourceFormatFile);
                }
            }
            catch
            {
                // 一時ファイルの削除失敗は無視
            }
        }
    }

    private void LogRunSummary(string jobLabel, string label, YtDlpRunResult run)
    {
        if (string.IsNullOrWhiteSpace(run.StdOutSummary))
        {
            return;
        }

        _logger.Info(
            $"{label} {jobLabel} / 終了コード={run.ExitCode} 経過={run.Elapsed.TotalSeconds:F1}秒{Environment.NewLine}" +
            run.StdOutSummary.TrimEnd());
    }

    private void CleanupFallbackLeftovers(
        string jobLabel,
        string outputPath,
        string requestedFormat,
        bool firstRunFailed,
        DateTime processStartedAtUtc)
    {
        if (!firstRunFailed)
        {
            return;
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        var outputFileName = Path.GetFileName(outputPath);
        if (string.IsNullOrEmpty(outputDirectory)
            || string.IsNullOrEmpty(outputFileName)
            || !Directory.Exists(outputDirectory))
        {
            return;
        }

        var outputBaseName = GetOutputBaseName(outputFileName, requestedFormat);
        if (string.IsNullOrEmpty(outputBaseName))
        {
            return;
        }

        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(outputDirectory, $"{outputBaseName}.f*");
        }
        catch (Exception ex)
        {
            _logger.Warn($"yt-dlp 中間ファイル確認失敗 {jobLabel} / dir=\"{outputDirectory}\" / {ex.GetType().Name}: {ex.Message}");
            return;
        }

        foreach (var path in candidates)
        {
            var file = new FileInfo(path);
            if (!IsYtDlpIntermediateFile(file, outputBaseName, processStartedAtUtc))
            {
                continue;
            }

            try
            {
                var length = file.Length;
                file.Delete();
                _logger.Info($"yt-dlp 中間ファイル削除 {jobLabel} / path=\"{file.FullName}\" size={length}");
            }
            catch (Exception ex)
            {
                _logger.Warn($"yt-dlp 中間ファイル削除失敗 {jobLabel} / path=\"{file.FullName}\" / {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static string GetOutputBaseName(string outputFileName, string requestedFormat)
    {
        if (outputFileName.EndsWith(".%(ext)s", StringComparison.OrdinalIgnoreCase))
        {
            return outputFileName[..^".%(ext)s".Length];
        }

        var requestedExtension = "." + requestedFormat.TrimStart('.');
        return outputFileName.EndsWith(requestedExtension, StringComparison.OrdinalIgnoreCase)
            ? outputFileName[..^requestedExtension.Length]
            : Path.GetFileNameWithoutExtension(outputFileName);
    }

    private static bool IsYtDlpIntermediateFile(FileInfo file, string outputBaseName, DateTime processStartedAtUtc)
    {
        var name = file.Name;
        if (!name.StartsWith(outputBaseName + ".f", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = name[(outputBaseName.Length + 2)..];
        var dotIndex = suffix.IndexOf('.');
        if (dotIndex <= 0)
        {
            return false;
        }

        if (!suffix[..dotIndex].All(char.IsDigit))
        {
            return false;
        }

        var lowerBound = processStartedAtUtc.AddMinutes(-1);
        return file.CreationTimeUtc >= lowerBound || file.LastWriteTimeUtc >= lowerBound;
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
            var version = await YtDlpProcessRunner.RunAsync(ytDlpPath, new[] { "--version" }, CancellationToken.None);
            _cachedYtDlpVersion = string.IsNullOrWhiteSpace(version) ? "(不明)" : version.Trim();
        }
        catch
        {
            _cachedYtDlpVersion = "(取得失敗)";
        }
        return _cachedYtDlpVersion!;
    }

    private string? ResolveFfmpegPath(AppSettings settings)
    {
        return !string.IsNullOrEmpty(settings.FfmpegPath) && File.Exists(settings.FfmpegPath)
            ? settings.FfmpegPath
            : _getFfmpegPath();
    }

    private sealed record SourceFormat(string? Ext, string Vcodec, string Acodec);

    private sealed record ActualFormat(string? Ext, string? Vcodec, string? Acodec);

    /// <summary>
    /// yt-dlpが書き出したソースフォーマット("ext|vcodec|acodec"形式)を読み取る。
    /// video:フェーズは再試行で複数行になり得るため、最後の有効行(=最終的に採用された選択)を採用する。
    /// </summary>
    private static SourceFormat? ReadSourceFormat(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var line = File.ReadAllLines(path).LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (string.IsNullOrEmpty(line))
            {
                return null;
            }

            var parts = line.Split('|');
            var ext = parts.Length > 0 ? parts[0].Trim() : null;
            return new SourceFormat(
                Ext: string.IsNullOrEmpty(ext) ? null : ext,
                Vcodec: parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1].Trim() : "none",
                Acodec: parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2].Trim() : "none");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// ffprobeで実ファイルの動画・音声コーデックを取得する。ffmpegと同ディレクトリのffprobeを想定。
    /// 動画・音声を1回のプロセスで取得する(codec_type,codec_nameのCSV)。
    /// ffprobeが無い場合は拡張子のみ返す。
    /// </summary>
    private async Task<ActualFormat?> ProbeFileFormatAsync(string? filePath, string? ffmpegPath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

        var ffprobePath = ResolveFfprobePath(ffmpegPath);
        if (string.IsNullOrEmpty(ffprobePath) || !File.Exists(ffprobePath))
        {
            // ffprobeが無い場合はコンテナ(拡張子)のみで比較する
            return new ActualFormat(ext, null, null);
        }

        try
        {
            // 各ストリームを codec と type のCSVで1回だけ取得する。
            // ffprobeは -show_entries の指定順と異なる列順で返すことがあるため、
            // 後段のパースでは video/audio がどちらの列にあるかを見て判定する。
            // 例:
            //   h264,video
            //   aac,audio
            var psi = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v error -show_entries stream=codec_type,codec_name -of csv=p=0 \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return new ActualFormat(ext, null, null);
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(CancellationToken.None);

            string? vcodec = null;
            string? acodec = null;
            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (!TryParseFfprobeCodecLine(line, out var type, out var codec))
                {
                    continue;
                }

                if (type == "video" && vcodec == null)
                {
                    vcodec = codec;
                }
                else if (type == "audio" && acodec == null)
                {
                    acodec = codec;
                }
            }

            return new ActualFormat(
                ext,
                string.IsNullOrEmpty(vcodec) ? "none" : vcodec,
                string.IsNullOrEmpty(acodec) ? "none" : acodec);
        }
        catch
        {
            return new ActualFormat(ext, null, null);
        }
    }

    private static bool TryParseFfprobeCodecLine(string line, out string type, out string codec)
    {
        type = string.Empty;
        codec = string.Empty;

        var fields = line.Split(',');
        if (fields.Length < 2)
        {
            return false;
        }

        var first = fields[0].Trim();
        var second = fields[1].Trim();
        if (IsMediaStreamType(first))
        {
            type = first.ToLowerInvariant();
            codec = second;
            return !string.IsNullOrEmpty(codec);
        }

        if (IsMediaStreamType(second))
        {
            type = second.ToLowerInvariant();
            codec = first;
            return !string.IsNullOrEmpty(codec);
        }

        return false;
    }

    private static bool IsMediaStreamType(string value)
    {
        return value.Equals("video", StringComparison.OrdinalIgnoreCase)
            || value.Equals("audio", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveFfprobePath(string? ffmpegPath)
    {
        if (string.IsNullOrEmpty(ffmpegPath))
        {
            return null;
        }

        var dir = Path.GetDirectoryName(ffmpegPath);
        if (string.IsNullOrEmpty(dir))
        {
            return null;
        }

        return Path.Combine(dir, "ffprobe.exe");
    }

    /// <summary>
    /// yt-dlp / ffprobe でのコーデック名表記差を吸収して比較可能にする。
    /// </summary>
    private static string NormalizeCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return "none";
        }

        var c = codec.ToLowerInvariant().Trim();

        // 動画コーデック
        if (c.StartsWith("avc1") || c == "h264") return "h264";
        if (c.StartsWith("hev1") || c.StartsWith("hvc1") || c == "hevc" || c == "h265") return "hevc";
        if (c.StartsWith("av01") || c == "av1") return "av1";
        if (c.StartsWith("vp9") || c == "vp09") return "vp9";
        if (c.StartsWith("vp8") || c == "vp08") return "vp8";

        // 音声コーデック
        if (c.StartsWith("mp4a") || c == "aac") return "aac";
        if (c == "opus") return "opus";
        if (c == "vorbis") return "vorbis";
        if (c == "mp3" || c == "mp3float") return "mp3";
        if (c.StartsWith("ac-3") || c == "ac3") return "ac3";

        if (c == "none" || c == "null") return "none";
        return c;
    }

    /// <summary>
    /// ソース(yt-dlp選択)と実ファイル(ffprobe)のフォーマットを比較し、結果をログとジョブへ記録する。
    /// </summary>
    private void CompareAndLogFormats(DownloadJob job)
    {
        var jobLabel = YtDlpFailureFormatter.BuildJobLabel(job);
        var requestedFormat = YtDlpArgumentBuilder.NormalizeFormat(job.Format);
        var isAudioOnly = YtDlpArgumentBuilder.IsAudioFormat(requestedFormat);

        // 情報が一切取れていない場合はスキップ
        if (string.IsNullOrEmpty(job.SourceExt) && string.IsNullOrEmpty(job.ActualExt))
        {
            _logger.Info($"フォーマット確認 {jobLabel} / 情報取得不可（スキップ）");
            return;
        }

        var srcV = NormalizeCodec(job.SourceVcodec);
        var srcA = NormalizeCodec(job.SourceAcodec);
        var actV = NormalizeCodec(job.ActualVcodec);
        var actA = NormalizeCodec(job.ActualAcodec);

        // mp3/wav は再エンコードが前提のため、音声codec・コンテナの不一致は正常とみなす
        bool isExpectedConversion = isAudioOnly && (requestedFormat == "mp3" || requestedFormat == "wav");

        // コンテナ比較（出力コンテナ拡張子 ≠ 実ファイル拡張子）。参考情報程度。
        bool containerMismatch = !string.IsNullOrEmpty(job.SourceExt)
            && !string.IsNullOrEmpty(job.ActualExt)
            && !job.SourceExt.Equals(job.ActualExt, StringComparison.OrdinalIgnoreCase);

        // 動画コーデック比較（音声のみの場合は対象外）
        bool videoMismatch = !isAudioOnly
            && srcV != "none" && actV != "none"
            && srcV != actV;

        // 音声コーデック比較（mp3/wav再エンコードは対象外）
        bool audioMismatch = !isExpectedConversion
            && srcA != "none" && actA != "none"
            && srcA != actA;

        bool isMismatch = (containerMismatch && !isExpectedConversion) || videoMismatch || audioMismatch;
        job.HasFormatMismatch = isMismatch;

        var sourceDesc = BuildFormatDescription(job.SourceExt, srcV, srcA);
        var actualDesc = BuildFormatDescription(job.ActualExt, actV, actA);

        if (isMismatch)
        {
            var reasons = new List<string>();
            if (containerMismatch && !isExpectedConversion)
            {
                reasons.Add($"コンテナ不一致({job.SourceExt}→{job.ActualExt})");
            }
            if (videoMismatch)
            {
                reasons.Add($"動画codec不一致({srcV}→{actV})");
            }
            if (audioMismatch)
            {
                reasons.Add($"音声codec不一致({srcA}→{actA})");
            }

            var reason = string.Join(", ", reasons);
            _logger.Warn($"フォーマット不一致 {jobLabel} / ソース={sourceDesc} → ダウンロード={actualDesc} / {reason}");

            job.FormatMismatchTooltip =
                "フォーマットが一致しません\n" +
                $"ソース: {sourceDesc}\n" +
                $"ダウンロード: {actualDesc}\n" +
                $"差異: {reason}";
        }
        else
        {
            _logger.Info($"フォーマット一致 {jobLabel} / ソース={sourceDesc} → ダウンロード={actualDesc}");

            // 音声変換があった場合は警告ではなく情報としてツールチップに出す
            if (isExpectedConversion && containerMismatch)
            {
                job.FormatMismatchTooltip = $"音声変換: {sourceDesc} → {actualDesc}";
            }
        }
    }

    private static string BuildFormatDescription(string? ext, string normalizedVcodec, string normalizedAcodec)
    {
        var desc = ext ?? "?";
        if (normalizedVcodec != "none")
        {
            desc += $"/{normalizedVcodec}";
        }
        if (normalizedAcodec != "none")
        {
            desc += $"/{normalizedAcodec}";
        }
        return desc;
    }
}
