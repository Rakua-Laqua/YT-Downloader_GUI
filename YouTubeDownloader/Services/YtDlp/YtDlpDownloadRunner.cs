using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YouTubeDownloader.Services;

internal static class YtDlpDownloadRunner
{
    private const int MaxStdErrChars = 20_000;
    private const int MaxSummaryChars = 8_000;
    private const int MaxDiagnosticsChars = 12_000;

    /// <summary>
    /// yt-dlpのダウンロードプロセスを起動し、stdoutから進捗を報告しながら終了コードとstderrを返す。
    /// 初回実行と403リトライで共通。
    /// </summary>
    public static async Task<YtDlpRunResult> RunDownloadProcessAsync(
        string ytDlpPath,
        IEnumerable<string> arguments,
        IProgress<ProgressInfo>? progress,
        string? conversionProgressFile,
        int durationSeconds,
        CancellationToken cancellationToken,
        ILoggingService? logger = null)
    {
        // cookieは元ファイルを汚さないよう一時コピーに差し替えて渡す（using でプロセス終了後に破棄）
        using var cookieScope = YtDlpCookieProtector.Begin(arguments, logger);
        var psi = YtDlpProcessRunner.CreateStartInfo(ytDlpPath, cookieScope.Arguments);

        // 失敗時に「どのタイミングで落ちたか」を記録するため、実行時間を計測する
        var stopwatch = Stopwatch.StartNew();

        using var process = new Process { StartInfo = psi };
        process.Start();
        using var killRegistration = YtDlpProcessRunner.RegisterProcessKillOnCancellation(process, cancellationToken);

        // 変換進捗ポーラーのキャンセル制御（ExtractAudio開始時に起動する）
        using var conversionPollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var outputProcessor = new DownloadOutputProcessor(progress, conversionProgressFile, durationSeconds, conversionPollCts.Token);

        // 出力を読み取りながら進捗を報告
        var outputTask = Task.Run(async () =>
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    break;
                }

                outputProcessor.ProcessLine(line);
            }
        });

        var errorOutput = new StringBuilder();
        var errorTask = Task.Run(async () =>
        {
            while (true)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    break;
                }

                AppendLimitedLine(errorOutput, line, MaxStdErrChars);
            }
        });

        try
        {
            await Task.WhenAll(outputTask, errorTask);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            YtDlpProcessRunner.KillProcessTree(process);
            throw;
        }
        finally
        {
            // 変換ポーラーを停止して終了を待つ
            conversionPollCts.Cancel();
            if (outputProcessor.ConversionPollTask != null)
            {
                try
                {
                    await outputProcessor.ConversionPollTask;
                }
                catch
                {
                    // ポーラーの後始末で例外は無視
                }
            }
        }

        await process.WaitForExitAsync(CancellationToken.None);
        stopwatch.Stop();
        cancellationToken.ThrowIfCancellationRequested();

        return new YtDlpRunResult
        {
            ExitCode = process.ExitCode,
            StdErr = errorOutput.ToString(),
            StdOutSummary = outputProcessor.StdOutSummary,
            StdOutDiagnostics = outputProcessor.StdOutDiagnostics,
            LastPhase = outputProcessor.LastPhase,
            Elapsed = stopwatch.Elapsed
        };
    }

    private sealed class DownloadOutputProcessor
    {
        private readonly IProgress<ProgressInfo>? _progress;
        private readonly string? _conversionProgressFile;
        private readonly int _durationSeconds;
        private readonly CancellationToken _conversionCancellationToken;
        private readonly bool _canTrackConversion;
        private readonly StringBuilder _outputSummary = new();
        private readonly HashSet<string> _outputSummaryLines = new(StringComparer.Ordinal);
        private readonly StringBuilder _outputDiagnostics = new();
        private string _currentPhaseStatus = "動画データダウンロード中";

        public DownloadOutputProcessor(
            IProgress<ProgressInfo>? progress,
            string? conversionProgressFile,
            int durationSeconds,
            CancellationToken conversionCancellationToken)
        {
            _progress = progress;
            _conversionProgressFile = conversionProgressFile;
            _durationSeconds = durationSeconds;
            _conversionCancellationToken = conversionCancellationToken;
            _canTrackConversion = !string.IsNullOrEmpty(conversionProgressFile) && durationSeconds > 0;
        }

        public Task? ConversionPollTask { get; private set; }
        public string LastPhase { get; private set; } = "(処理開始前)";
        public string StdOutSummary => _outputSummary.ToString();
        public string StdOutDiagnostics => _outputDiagnostics.ToString();

        public void ProcessLine(string line)
        {
            AppendSummaryLine(_outputSummary, _outputSummaryLines, line);
            AppendDiagnosticLine(_outputDiagnostics, line);

            UpdatePhase(line);
            if (StartConversionPollingIfNeeded(line))
            {
                return;
            }

            ReportProgress(line);
        }

        private void UpdatePhase(string line)
        {
            var detectedPhase = DetectPhaseName(line);
            if (detectedPhase == null)
            {
                return;
            }

            LastPhase = detectedPhase;
            if (detectedPhase.Contains("ダウンロード"))
            {
                _currentPhaseStatus = detectedPhase;
            }
        }

        private bool StartConversionPollingIfNeeded(string line)
        {
            if (!_canTrackConversion
                || ConversionPollTask != null
                || !line.Contains("[ExtractAudio] Destination:"))
            {
                return false;
            }

            ConversionPollTask = PollConversionProgressAsync(_conversionProgressFile!, _durationSeconds, _progress, _conversionCancellationToken);
            _progress?.Report(new ProgressInfo { Percentage = 0, Status = "音声変換中...", IsPostProcessing = false });
            return true;
        }

        private void ReportProgress(string line)
        {
            if (_progress == null)
            {
                return;
            }

            var progressInfo = ParseProgressLine(line);
            if (progressInfo == null)
            {
                return;
            }

            if (progressInfo.Status == "ダウンロード中")
            {
                progressInfo.Status = _currentPhaseStatus;
            }
            _progress.Report(progressInfo);
        }
    }

    private static void AppendSummaryLine(StringBuilder outputSummary, HashSet<string> outputSummaryLines, string line)
    {
        if (!IsSummaryOutputLine(line))
        {
            return;
        }

        if (outputSummary.Length > MaxSummaryChars || !outputSummaryLines.Add(line))
        {
            return;
        }

        AppendLimitedLine(outputSummary, line, MaxSummaryChars);
    }

    private static bool IsSummaryOutputLine(string line)
    {
        return (line.StartsWith("[info]", StringComparison.OrdinalIgnoreCase)
                && line.Contains("Downloading", StringComparison.OrdinalIgnoreCase)
                && line.Contains("format(s)", StringComparison.OrdinalIgnoreCase))
            || line.Contains("[download] Destination:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("has already been downloaded", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[Merger] Merging formats into", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[ExtractAudio] Destination:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[MoveFiles] Moving file", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendDiagnosticLine(StringBuilder outputDiagnostics, string line)
    {
        if (!IsDiagnosticOutputLine(line))
        {
            return;
        }

        // 同じタイムアウト行が大量に出てもログを肥大化させない。
        if (outputDiagnostics.Length > MaxDiagnosticsChars)
        {
            return;
        }

        AppendLimitedLine(outputDiagnostics, line, MaxDiagnosticsChars);
    }

    private static void AppendLimitedLine(StringBuilder builder, string line, int maxChars)
    {
        if (builder.Length >= maxChars)
        {
            return;
        }

        var remaining = maxChars - builder.Length;
        if (line.Length + Environment.NewLine.Length <= remaining)
        {
            builder.AppendLine(line);
            return;
        }

        if (remaining > Environment.NewLine.Length)
        {
            builder.Append(line, 0, remaining - Environment.NewLine.Length);
        }
        builder.AppendLine();
    }

    private static bool IsDiagnosticOutputLine(string line)
    {
        return line.Contains("Got error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Retrying", StringComparison.OrdinalIgnoreCase)
            || line.Contains("HTTP Error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Read timed out", StringComparison.OrdinalIgnoreCase)
            || line.Contains("bytes read", StringComparison.OrdinalIgnoreCase)
            || line.Contains("more expected", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Unable to download", StringComparison.OrdinalIgnoreCase)
            || line.Contains("fragment", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase);
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
}
