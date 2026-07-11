using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using YouTubeDownloader.Models;

namespace YouTubeDownloader.Services;

/// <summary>
/// ダウンロードジョブの管理を行うマネージャー
/// </summary>
public interface IDownloadManager
{
    event EventHandler<DownloadJobEventArgs>? JobProgressChanged;
    event EventHandler<DownloadJobEventArgs>? JobStatusChanged;

    IReadOnlyList<DownloadJob> GetAllJobs();
    void Enqueue(DownloadJob job);
    void Cancel(Guid jobId);
    void CancelAll();
    void Retry(Guid jobId);
    void ClearCompleted();
    void ClearAll();
}

public class DownloadJobEventArgs : EventArgs
{
    public DownloadJob Job { get; }
    public DownloadJobEventArgs(DownloadJob job) => Job = job;
}

public class DownloadManager : IDownloadManager, IDisposable
{
    private readonly IYtDlpClient _ytDlpClient;
    private readonly IMetadataRepository _metadataRepository;
    private readonly ISettingsRepository _settingsRepository;
    private readonly ILoggingService _logger;
    private readonly List<DownloadJob> _allJobs = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _cancellationTokens = new();
    private readonly Queue<TaskCompletionSource> _slotWaiters = new();
    private readonly object _lock = new();

    /// <summary>同時ダウンロード数の許容範囲</summary>
    public const int MinConcurrency = 1;
    public const int MaxConcurrencyLimit = 8;

    /// <summary>現在有効な同時ダウンロード数（設定保存で動的に変わる）</summary>
    private int _maxConcurrency;
    private int _runningDownloads;
    private bool _disposed;

    public event EventHandler<DownloadJobEventArgs>? JobProgressChanged;
    public event EventHandler<DownloadJobEventArgs>? JobStatusChanged;

    public DownloadManager(IYtDlpClient ytDlpClient, IMetadataRepository metadataRepository, ISettingsRepository settingsRepository, ILoggingService logger)
    {
        _ytDlpClient = ytDlpClient;
        _metadataRepository = metadataRepository;
        _settingsRepository = settingsRepository;
        _logger = logger;

        _maxConcurrency = ClampConcurrency(_settingsRepository.Load().MaxConcurrentDownloads);

        _settingsRepository.SettingsSaved += OnSettingsSaved;
    }

    public static int ClampConcurrency(int value)
    {
        if (value < MinConcurrency) return MinConcurrency;
        if (value > MaxConcurrencyLimit) return MaxConcurrencyLimit;
        return value;
    }

    private void OnSettingsSaved(object? sender, AppSettings settings)
    {
        ApplyMaxConcurrency(ClampConcurrency(settings.MaxConcurrentDownloads));
    }

    /// <summary>
    /// 同時実行数を動的に変更する。実行中のジョブは中断せず、以降のジョブ開始から新しい上限が効く。
    /// </summary>
    private void ApplyMaxConcurrency(int newMax)
    {
        lock (_lock)
        {
            if (_disposed || newMax == _maxConcurrency)
            {
                return;
            }

            _maxConcurrency = newMax;
            ReleaseQueuedSlotsIfPossibleLocked();
        }
    }

    private async Task WaitForDownloadSlotAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource waiter;
        lock (_lock)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(DownloadManager));
            }

            if (_runningDownloads < _maxConcurrency)
            {
                _runningDownloads++;
                return;
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _slotWaiters.Enqueue(waiter);
        }

        using var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
        await waiter.Task;
    }

    private void ReleaseDownloadSlot()
    {
        lock (_lock)
        {
            if (_runningDownloads > 0)
            {
                _runningDownloads--;
            }

            ReleaseQueuedSlotsIfPossibleLocked();
        }
    }

    private void ReleaseQueuedSlotsIfPossibleLocked()
    {
        while (!_disposed && _runningDownloads < _maxConcurrency && _slotWaiters.Count > 0)
        {
            var waiter = _slotWaiters.Dequeue();
            if (waiter.Task.IsCompleted)
            {
                continue;
            }

            if (waiter.TrySetResult())
            {
                _runningDownloads++;
            }
        }
    }

    public IReadOnlyList<DownloadJob> GetAllJobs()
    {
        lock (_lock)
        {
            return _allJobs.ToArray();
        }
    }

    public void Enqueue(DownloadJob job)
    {
        lock (_lock)
        {
            _allJobs.Add(job);
        }

        job.Status = DownloadStatus.Pending;
        JobStatusChanged?.Invoke(this, new DownloadJobEventArgs(job));

        StartJobProcessing(job);
    }

    private void StartJobProcessing(DownloadJob job)
    {
        _ = Task.Run(() => ProcessJobAsync(job));
    }

    private async Task ProcessJobAsync(DownloadJob job)
    {
        var cts = CreateAndRegisterCancellationToken(job);
        var semaphoreAcquired = false;

        // 「どのタイミングで」を追えるよう、キュー待機(セマフォ取得)にかかった時間も記録する
        var waitStopwatch = Stopwatch.StartNew();
        try
        {
            await WaitForDownloadSlotAsync(cts.Token);
            semaphoreAcquired = true;
            cts.Token.ThrowIfCancellationRequested();
            waitStopwatch.Stop();

            MarkJobRunning(job, waitStopwatch.Elapsed);
            await _ytDlpClient.DownloadAsync(job, CreateProgressReporter(job), cts.Token);
            await CompleteJobAsync(job);
        }
        catch (OperationCanceledException)
        {
            MarkJobCanceled(job);
        }
        catch (YtDlpDownloadException ex)
        {
            _logger.Error($"ジョブ失敗(yt-dlp) {YtDlpFailureFormatter.BuildJobLabel(job)}", ex);
            if (string.IsNullOrWhiteSpace(job.FailureDetail) && !string.IsNullOrWhiteSpace(ex.FailureDetail))
            {
                job.FailureDetail = ex.FailureDetail;
            }
            MarkJobFailed(job, ex.Message);
        }
        catch (Exception ex)
        {
            // yt-dlp固有の失敗詳細は YtDlpClient 側で記録済み。ここは呼び出し境界として、
            // 例外の型とスタックトレース(=どのC#処理で発生したか)を残す。
            // 例: メタデータ保存の失敗もここに来るため、スタックトレースで発生箇所を特定できる。
            _logger.Error($"ジョブ失敗 {YtDlpFailureFormatter.BuildJobLabel(job)}", ex);
            MarkJobFailed(job, ex.Message);
        }
        finally
        {
            CleanupJobProcessing(job, cts, semaphoreAcquired);
        }
    }

    private CancellationTokenSource CreateAndRegisterCancellationToken(DownloadJob job)
    {
        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            _cancellationTokens[job.Id] = cts;
            if (job.Status == DownloadStatus.Canceled)
            {
                cts.Cancel();
            }
        }
        return cts;
    }

    private void MarkJobRunning(DownloadJob job, TimeSpan queueWait)
    {
        job.Status = DownloadStatus.Running;
        job.StartedAt = DateTime.Now;
        job.Progress = 0;
        job.IsPostProcessing = false;
        _logger.Info($"ジョブ開始 {YtDlpFailureFormatter.BuildJobLabel(job)} / キュー待機 {queueWait.TotalSeconds:F1}秒");
        JobStatusChanged?.Invoke(this, new DownloadJobEventArgs(job));
    }

    private IProgress<ProgressInfo> CreateProgressReporter(DownloadJob job)
    {
        return new Progress<ProgressInfo>(info =>
        {
            job.Progress = info.Percentage;
            job.IsPostProcessing = info.IsPostProcessing;
            if (!string.IsNullOrWhiteSpace(info.Status))
            {
                job.StatusMessage = info.Status;
            }

            JobProgressChanged?.Invoke(this, new DownloadJobEventArgs(job));
        });
    }

    private async Task CompleteJobAsync(DownloadJob job)
    {
        job.Progress = 100;
        job.IsPostProcessing = false;
        job.StatusMessage = null;
        job.CompletedAt = DateTime.Now;
        job.VideoMetadata.DownloadedAt = DateTime.Now;
        job.VideoMetadata.Format = job.Format;

        try
        {
            await _metadataRepository.SaveVideoMetadataAsync(job.VideoMetadata);
            job.Status = DownloadStatus.Completed;
            job.ErrorMessage = null;
        }
        catch (Exception ex)
        {
            _logger.Error($"メタデータ保存に失敗しました(ダウンロードは完了) {YtDlpFailureFormatter.BuildJobLabel(job)}", ex);
            job.Status = DownloadStatus.CompletedWithWarning;
            job.ErrorMessage = "ダウンロードは完了しましたが、履歴を保存できませんでした。ログを確認してください。";
        }

        JobStatusChanged?.Invoke(this, new DownloadJobEventArgs(job));
    }

    private void MarkJobCanceled(DownloadJob job)
    {
        _logger.Info($"ジョブをキャンセルしました {YtDlpFailureFormatter.BuildJobLabel(job)}");
        var shouldNotify = job.Status != DownloadStatus.Canceled;
        job.Status = DownloadStatus.Canceled;
        if (shouldNotify)
        {
            JobStatusChanged?.Invoke(this, new DownloadJobEventArgs(job));
        }
    }

    private void MarkJobFailed(DownloadJob job, string errorMessage)
    {
        job.Status = DownloadStatus.Failed;
        job.ErrorMessage = errorMessage;
        JobStatusChanged?.Invoke(this, new DownloadJobEventArgs(job));
    }

    private void CleanupJobProcessing(DownloadJob job, CancellationTokenSource cts, bool semaphoreAcquired)
    {
        lock (_lock)
        {
            if (_cancellationTokens.TryGetValue(job.Id, out var currentCts) && ReferenceEquals(currentCts, cts))
            {
                _cancellationTokens.Remove(job.Id);
            }
        }
        cts.Dispose();
        if (semaphoreAcquired)
        {
            ReleaseDownloadSlot();
        }
    }

    public void Cancel(Guid jobId)
    {
        lock (_lock)
        {
            if (_cancellationTokens.TryGetValue(jobId, out var cts))
            {
                cts.Cancel();
            }
            else
            {
                // まだ開始していないジョブ
                var job = _allJobs.Find(j => j.Id == jobId);
                if (job != null && job.Status == DownloadStatus.Pending)
                {
                    job.Status = DownloadStatus.Canceled;
                    JobStatusChanged?.Invoke(this, new DownloadJobEventArgs(job));
                }
            }
        }
    }

    public void CancelAll()
    {
        lock (_lock)
        {
            // 実行中はトークンでキャンセル
            foreach (var cts in _cancellationTokens.Values)
            {
                cts.Cancel();
            }

            // 未開始（Pending）は即キャンセル扱い
            foreach (var job in _allJobs)
            {
                if (job.Status == DownloadStatus.Pending)
                {
                    job.Status = DownloadStatus.Canceled;
                    JobStatusChanged?.Invoke(this, new DownloadJobEventArgs(job));
                }
            }
        }
    }

    public void Retry(Guid jobId)
    {
        DownloadJob? jobToRetry = null;
        lock (_lock)
        {
            var job = _allJobs.Find(j => j.Id == jobId);
            if (job != null && (job.Status == DownloadStatus.Failed || job.Status == DownloadStatus.Canceled))
            {
                job.Status = DownloadStatus.Pending;
                job.ErrorMessage = null;
                job.Progress = 0;
                jobToRetry = job;
            }
        }

        if (jobToRetry != null)
        {
            JobStatusChanged?.Invoke(this, new DownloadJobEventArgs(jobToRetry));
            StartJobProcessing(jobToRetry);
        }
    }

    public void ClearCompleted()
    {
        lock (_lock)
        {
            _allJobs.RemoveAll(j => j.Status is DownloadStatus.Completed or DownloadStatus.CompletedWithWarning or DownloadStatus.Canceled);
        }
    }

    public void ClearAll()
    {
        // まず全キャンセル
        CancelAll();

        lock (_lock)
        {
            _allJobs.Clear();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var cts in _cancellationTokens.Values)
            {
                cts.Cancel();
            }
            _cancellationTokens.Clear();
            while (_slotWaiters.Count > 0)
            {
                _slotWaiters.Dequeue().TrySetCanceled();
            }
        }

        _settingsRepository.SettingsSaved -= OnSettingsSaved;
    }
}
