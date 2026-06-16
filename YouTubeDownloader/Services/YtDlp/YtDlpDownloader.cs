using System;
using System.IO;
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

        var ffmpegPath = ResolveFfmpegPath(settings);
        var arguments = YtDlpArgumentBuilder.BuildDownloadArguments(
            job,
            settings,
            outputPath,
            requestedFormat,
            ffmpegPath,
            conversionProgressFile,
            downloadInfoFile);

        try
        {
            _logger.Info($"yt-dlp 実行 {jobLabel}: {ytDlpPath} {YtDlpFailureFormatter.FormatArgumentsForLog(arguments)}");
            var firstRun = await YtDlpDownloadRunner.RunDownloadProcessAsync(ytDlpPath, arguments, progress, conversionProgressFile, durationSeconds, cancellationToken);

            if (firstRun.ExitCode != 0)
            {
                _logger.Warn($"yt-dlp 初回失敗 {jobLabel} / 終了コード={firstRun.ExitCode} 失敗フェーズ={firstRun.LastPhase} 経過={firstRun.Elapsed.TotalSeconds:F1}秒。フォールバックclientで再試行します。");

                // 403 や UNPLAYABLE（「この動画は…」）はデフォルトclientの問題であることが多い。
                // 別のplayer client群（tv等）で1回だけ再試行する。
                var retryArguments = YtDlpArgumentBuilder.BuildFallbackClientArguments(arguments, settings);

                _logger.Info($"yt-dlp 再試行 {jobLabel}: {ytDlpPath} {YtDlpFailureFormatter.FormatArgumentsForLog(retryArguments)}");
                var retryRun = await YtDlpDownloadRunner.RunDownloadProcessAsync(ytDlpPath, retryArguments, progress, conversionProgressFile, durationSeconds, cancellationToken);

                if (retryRun.ExitCode != 0)
                {
                    var firstReason = YtDlpFailureFormatter.ExtractMeaningfulError(firstRun.StdErr);
                    var retryReason = YtDlpFailureFormatter.ExtractMeaningfulError(retryRun.StdErr);

                    // 失敗の「どのタイミングで・なぜ」が後から追えるよう、両試行の終了コード・失敗フェーズ・
                    // 経過時間・実行コマンド・stderr全文・yt-dlpバージョンをまとめて記録する。
                    var ytDlpVersion = await GetYtDlpVersionForLogAsync(ytDlpPath);
                    var detail = YtDlpFailureFormatter.BuildDownloadFailureDetail(job, ytDlpVersion, arguments, retryArguments, firstRun, retryRun);
                    job.FailureDetail = detail;
                    _logger.Error($"ダウンロード失敗 {jobLabel}{Environment.NewLine}{detail}");

                    var combinedReason = $"【初回試行のエラー】:\n{firstReason}\n\n【再試行（フォールバック）のエラー】:\n{retryReason}";
                    throw new Exception($"ダウンロードに失敗しました:\n{combinedReason}");
                }

                _logger.Info($"yt-dlp 再試行成功 {jobLabel} / 経過={retryRun.Elapsed.TotalSeconds:F1}秒");
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
