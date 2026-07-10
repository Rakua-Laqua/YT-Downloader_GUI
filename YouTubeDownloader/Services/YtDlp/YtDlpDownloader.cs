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
    private readonly YtDlpFormatInspector _formatInspector;
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
        _formatInspector = new YtDlpFormatInspector(logger);
    }

    public async Task DownloadAsync(DownloadJob job, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken = default)
    {
        var jobLabel = YtDlpFailureFormatter.BuildJobLabel(job);
        _logger.Info($"ダウンロード開始 {jobLabel} / 形式={job.Format} 品質={job.Quality} 保存先=\"{job.SaveFolderPath}\"");

        ValidateJobUrl(job, jobLabel);

        var ytDlpPath = ResolveYtDlpPath(job, jobLabel);
        var settings = await PrepareYtDlpAndLoadSettingsAsync(job, jobLabel, ytDlpPath, cancellationToken).ConfigureAwait(false);
        var (outputPath, requestedFormat) = BuildOutputPathAndFormat(job, jobLabel, settings);

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
            firstRunFailed = await RunDownloadWithFallbackAsync(
                job,
                jobLabel,
                ytDlpPath,
                arguments,
                settings,
                progress,
                conversionProgressFile,
                durationSeconds,
                cancellationToken).ConfigureAwait(false);

            var infoResult = YtDlpOutputPathResolver.ReadDownloadInfoFile(downloadInfoFile);
            ApplyDownloadedFileInfo(job, settings, outputPath, requestedFormat, infoResult);

            _logger.Info($"ダウンロード成功 {jobLabel} / 出力=\"{job.VideoMetadata.LocalFilePath}\"");

            await _formatInspector.ApplyFormatInfoAsync(job, ffmpegPath, sourceFormatFile).ConfigureAwait(false);

            CleanupFallbackLeftovers(jobLabel, outputPath, requestedFormat, firstRunFailed, processStartedAtUtc);
        }
        finally
        {
            TryDeleteTempFile(conversionProgressFile);
            TryDeleteTempFile(downloadInfoFile);
            TryDeleteTempFile(sourceFormatFile);
        }
    }

    private void ValidateJobUrl(DownloadJob job, string jobLabel)
    {
        if (!string.IsNullOrEmpty(job.VideoMetadata.Url))
        {
            return;
        }

        const string msg = "動画URLが指定されていません";
        var detail = YtDlpFailureFormatter.BuildPreflightFailureDetail(job, "準備(URL検証)", msg, null);
        job.FailureDetail = detail;
        _logger.Error($"ダウンロード失敗 {jobLabel} / フェーズ=準備(URL検証): {msg}");
        throw new YtDlpDownloadException(msg, detail);
    }

    private string ResolveYtDlpPath(DownloadJob job, string jobLabel)
    {
        try
        {
            return _getYtDlpPath();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw BuildPreflightException(job, jobLabel, "準備(yt-dlp検出)", ex.Message, ex);
        }
    }

    private async Task<AppSettings> PrepareYtDlpAndLoadSettingsAsync(
        DownloadJob job,
        string jobLabel,
        string ytDlpPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await _updater.EnsureYtDlpUpdatedAsync(ytDlpPath, cancellationToken).ConfigureAwait(false);
            return _settingsRepository.Load();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw BuildPreflightException(job, jobLabel, "準備(yt-dlp更新/設定読み込み)", ex.Message, ex);
        }
    }

    private (string OutputPath, string RequestedFormat) BuildOutputPathAndFormat(
        DownloadJob job,
        string jobLabel,
        AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(job.SaveFolderPath);

            var template = YtDlpOutputPathResolver.BuildFilenameTemplate(settings.FilenameTemplate, job);
            var outputPath = YtDlpOutputPathResolver.BuildOutputPath(job.SaveFolderPath, template);
            var requestedFormat = YtDlpArgumentBuilder.NormalizeFormat(job.Format);
            return (outputPath, requestedFormat);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw BuildPreflightException(job, jobLabel, "準備(保存先/出力パス構築)", ex.Message, ex);
        }
    }

    private YtDlpDownloadException BuildPreflightException(
        DownloadJob job,
        string jobLabel,
        string phase,
        string reason,
        Exception exception)
    {
        var detail = YtDlpFailureFormatter.BuildPreflightFailureDetail(job, phase, reason, exception);
        job.FailureDetail = detail;
        _logger.Error($"ダウンロード失敗 {jobLabel} / フェーズ={phase}", exception);
        return new YtDlpDownloadException(reason, detail, innerException: exception);
    }

    private async Task<bool> RunDownloadWithFallbackAsync(
        DownloadJob job,
        string jobLabel,
        string ytDlpPath,
        List<string> arguments,
        AppSettings settings,
        IProgress<ProgressInfo>? progress,
        string? conversionProgressFile,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        _logger.Info($"yt-dlp 実行 {jobLabel}: {ytDlpPath} {YtDlpFailureFormatter.FormatArgumentsForLog(arguments)}");
        var firstRun = await YtDlpDownloadRunner.RunDownloadProcessAsync(ytDlpPath, arguments, progress, conversionProgressFile, durationSeconds, cancellationToken, _logger).ConfigureAwait(false);
        LogRunSummary(jobLabel, "yt-dlp 初回出力要約", firstRun);

        if (firstRun.ExitCode == 0)
        {
            return false;
        }

        if (!YtDlpFailureFormatter.ShouldRetryWithFallbackClient(firstRun))
        {
            var ytDlpVersionNoRetry = await GetYtDlpVersionForLogAsync(ytDlpPath).ConfigureAwait(false);
            var singleDetail = YtDlpFailureFormatter.BuildDownloadFailureDetail(job, ytDlpVersionNoRetry, arguments, firstRun);
            job.FailureDetail = singleDetail;
            _logger.Error($"ダウンロード失敗（別clientでも回復不能と判断し再試行せず） {jobLabel}{Environment.NewLine}{singleDetail}");
            throw new YtDlpDownloadException(
                $"ダウンロードに失敗しました:\n{YtDlpFailureFormatter.DescribeError(firstRun)}",
                singleDetail);
        }

        _logger.Warn($"yt-dlp 初回失敗 {jobLabel} / 終了コード={firstRun.ExitCode} 失敗フェーズ={firstRun.LastPhase} 経過={firstRun.Elapsed.TotalSeconds:F1}秒。フォールバックclientで再試行します。");

        var retryArguments = YtDlpArgumentBuilder.BuildFallbackClientArguments(arguments, settings);

        _logger.Info($"yt-dlp 再試行 {jobLabel}: {ytDlpPath} {YtDlpFailureFormatter.FormatArgumentsForLog(retryArguments)}");
        var retryRun = await YtDlpDownloadRunner.RunDownloadProcessAsync(ytDlpPath, retryArguments, progress, conversionProgressFile, durationSeconds, cancellationToken, _logger).ConfigureAwait(false);
        LogRunSummary(jobLabel, "yt-dlp 再試行出力要約", retryRun);

        if (retryRun.ExitCode != 0)
        {
            var ytDlpVersion = await GetYtDlpVersionForLogAsync(ytDlpPath).ConfigureAwait(false);
            var detail = YtDlpFailureFormatter.BuildDownloadFailureDetail(job, ytDlpVersion, arguments, retryArguments, firstRun, retryRun);
            job.FailureDetail = detail;
            _logger.Error($"ダウンロード失敗 {jobLabel}{Environment.NewLine}{detail}");

            var combinedReason = YtDlpFailureFormatter.BuildUserFacingDownloadReason(firstRun, retryRun);
            throw new YtDlpDownloadException(
                $"ダウンロードに失敗しました:\n{combinedReason}",
                detail,
                retriedWithFallbackClient: true);
        }

        _logger.Info($"yt-dlp 再試行成功 {jobLabel} / 経過={retryRun.Elapsed.TotalSeconds:F1}秒");
        _logger.Warn(
            $"yt-dlp 初回失敗診断 {jobLabel}{Environment.NewLine}" +
            YtDlpFailureFormatter.BuildAttemptDiagnosticDetail("初回試行", arguments, firstRun));

        return true;
    }

    private static void ApplyDownloadedFileInfo(
        DownloadJob job,
        AppSettings settings,
        string outputPath,
        string requestedFormat,
        YtDlpDownloadInfoResult infoResult)
    {
        if (!string.IsNullOrEmpty(infoResult.FilePath) && File.Exists(infoResult.FilePath))
        {
            job.VideoMetadata.LocalFilePath = infoResult.FilePath;
        }
        else
        {
            YtDlpOutputPathResolver.UpdateLocalFilePath(job, outputPath, requestedFormat);
        }

        if (!settings.SetFileDateToPublishDate
            || string.IsNullOrEmpty(job.VideoMetadata.LocalFilePath)
            || !File.Exists(job.VideoMetadata.LocalFilePath)
            || !infoResult.PublishTime.HasValue)
        {
            return;
        }

        var publishTime = infoResult.PublishTime.Value;
        if (publishTime.Kind == DateTimeKind.Utc)
        {
            File.SetLastWriteTimeUtc(job.VideoMetadata.LocalFilePath, publishTime);
        }
        else
        {
            File.SetLastWriteTime(job.VideoMetadata.LocalFilePath, publishTime);
        }
    }

    private static void TryDeleteTempFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 一時ファイルの削除失敗は無視
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

}

/// <summary>
/// ダウンロード後のファイル形式を検査し、ソース形式との比較結果をジョブとログへ反映する。
/// </summary>
internal sealed class YtDlpFormatInspector
{
    private static readonly TimeSpan FfprobeTimeout = TimeSpan.FromSeconds(10);
    private readonly ILoggingService _logger;

    public YtDlpFormatInspector(ILoggingService logger)
    {
        _logger = logger;
    }

    private sealed record SourceFormat(string? Ext, string Vcodec, string Acodec);

    private sealed record ActualFormat(string? Ext, string? Vcodec, string? Acodec);

    private sealed record FfprobeOutput(string StandardOutput, string StandardError);

    public async Task ApplyFormatInfoAsync(DownloadJob job, string? ffmpegPath, string sourceFormatFile)
    {
        var sourceFmt = ReadSourceFormat(sourceFormatFile);
        job.SourceExt = sourceFmt?.Ext;
        job.SourceVcodec = sourceFmt?.Vcodec;
        job.SourceAcodec = sourceFmt?.Acodec;

        var actualFmt = await ProbeFileFormatAsync(job.VideoMetadata.LocalFilePath, ffmpegPath).ConfigureAwait(false);
        job.ActualExt = actualFmt?.Ext;
        job.ActualVcodec = actualFmt?.Vcodec;
        job.ActualAcodec = actualFmt?.Acodec;

        CompareAndLogFormats(job);
    }

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
            var output = await RunFfprobeAsync(ffprobePath, filePath);
            return ParseFfprobeOutput(ext, output.StandardOutput);
        }
        catch (Exception ex)
        {
            _logger.Warn($"ffprobeによるフォーマット確認に失敗しました: {ex.Message}");
            return new ActualFormat(ext, null, null);
        }
    }

    private static async Task<FfprobeOutput> RunFfprobeAsync(string ffprobePath, string filePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("stream=codec_type,codec_name");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("csv=p=0");
        psi.ArgumentList.Add(filePath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("ffprobeプロセスを開始できませんでした。");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = new CancellationTokenSource(FfprobeTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(outputTask, errorTask);
            }
            catch
            {
                // kill後のストリーム終了待ち失敗は、タイムアウトの報告を優先する
            }

            throw new TimeoutException($"ffprobeが{FfprobeTimeout.TotalSeconds:F0}秒以内に終了しませんでした。");
        }

        await Task.WhenAll(outputTask, errorTask);
        var standardOutput = outputTask.Result;
        var standardError = errorTask.Result;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffprobeが終了コード {process.ExitCode} を返しました。{standardError.Trim()}");
        }

        return new FfprobeOutput(standardOutput, standardError);
    }

    private static ActualFormat ParseFfprobeOutput(string ext, string output)
    {
        // ffprobeは -show_entries の指定順と異なる列順で返すことがあるため、
        // video/audio がどちらの列にあるかを見て判定する。
        string? vcodec = null;
        string? acodec = null;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !TryParseFfprobeCodecLine(line, out var type, out var codec))
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

    private static void TryKillProcess(Process process)
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
            // ffprobe失敗時は拡張子ベースにフォールバックする
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
