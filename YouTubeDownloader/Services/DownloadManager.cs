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
    private readonly SemaphoreSlim _semaphore;
    private readonly object _lock = new();

    /// <summary>同時ダウンロード数の許容範囲</summary>
    public const int MinConcurrency = 1;
    public const int MaxConcurrencyLimit = 8;

    /// <summary>現在有効な同時ダウンロード数（設定保存で動的に変わる）</summary>
    private int _maxConcurrency;
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
        // 動的に許可数を増減できるよう、上限を固定しない形で生成する
        _semaphore = new SemaphoreSlim(_maxConcurrency);

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
    /// SemaphoreSlimは生成後にサイズ変更できないため、許可数の増減で実現する：
    /// 増やす場合は Release、減らす場合は空いた許可を吸収して新規開始を抑制する。
    /// </summary>
    private void ApplyMaxConcurrency(int newMax)
    {
        int delta;
        lock (_lock)
        {
            if (_disposed || newMax == _maxConcurrency)
            {
                return;
            }
            delta = newMax - _maxConcurrency;
            _maxConcurrency = newMax;
        }

        if (delta > 0)
        {
            try
            {
                _semaphore.Release(delta);
            }
            catch (ObjectDisposedException)
            {
                // 破棄済みなら何もしない
            }
        }
        else
        {
            for (var i = 0; i < -delta; i++)
            {
                _ = AbsorbPermitAsync();
            }
        }
    }

    /// <summary>許可を1つ取得したまま解放しないことで、実効的な同時実行上限を1下げる</summary>
    private async Task AbsorbPermitAsync()
    {
        try
        {
            await _semaphore.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            // 破棄済みなら何もしない
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

        _ = ProcessJobAsync(job);
    }

    private async Task ProcessJobAsync(DownloadJob job)
    {
        var cts = new CancellationTokenSource();
        var semaphoreAcquired = false;
        lock (_lock)
        {
            _cancellationTokens[job.Id] = cts;
            if (job.Status == DownloadStatus.Canceled)
            {
                cts.Cancel();
            }
        }

        // 「どのタイミングで」を追えるよう、キュー待機(セマフォ取得)にかかった時間も記録する
        var waitStopwatch = Stopwatch.StartNew();
        try
        {
            await _semaphore.WaitAsync(cts.Token);
            semaphoreAcquired = true;
            cts.Token.ThrowIfCancellationRequested();
            waitStopwatch.Stop();

            job.Status = DownloadStatus.Running;
            job.StartedAt = DateTime.Now;
            job.Progress = 0;
            job.IsPostProcessing = false;
            _logger.Info($"ジョブ開始 [{job.VideoMetadata.Title}] <{job.VideoMetadata.Url}> / キュー待機 {waitStopwatch.Elapsed.TotalSeconds:F1}秒");
            JobStatusChanged?.Invoke(this, new DownloadJobEventArgs(job));

            var progress = new Progress<ProgressInfo>(info =>
            {
                job.Progress = info.Percentage;
                job.IsPostProcessing = info.IsPostProcessing;
                if (!string.IsNullOrWhiteSpace(info.Status))
                {
                    job.StatusMessage = info.Status;
                }

                JobProgressChanged?.Invoke(this, new DownloadJobEventArgs(job));
            });

            await _ytDlpClient.DownloadAsync(job, progress, cts.Token);

            job.Status = DownloadStatus.Completed;
            job.Progress = 100;
            job.IsPostProcessing = false;
            job.StatusMessage = null;
            job.CompletedAt = DateTime.Now;
            job.VideoMetadata.DownloadedAt = DateTime.Now;
            job.VideoMetadata.Format = job.Format;

            // メタデータを保存
            await _metadataRepository.SaveVideoMetadataAsync(job.VideoMetadata);

            JobStatusChanged?.Invoke(this, new DownloadJobEventArgs(job));
        }
        catch (OperationCanceledException)
        {
            _logger.Info($"ジョブをキャンセルしました [{job.VideoMetadata.Title}] <{job.VideoMetadata.Url}>");
            var shouldNotify = job.Status != DownloadStatus.Canceled;
            job.Status = DownloadStatus.Canceled;
            if (shouldNotify)
            {
                JobStatusChanged?.Invoke(this, new DownloadJobEventArgs(job));
            }
        }
        catch (Exception ex)
        {
            // yt-dlp固有の失敗詳細は YtDlpClient 側で記録済み。ここは呼び出し境界として、
            // 例外の型とスタックトレース(=どのC#処理で発生したか)を残す。
            // 例: メタデータ保存の失敗もここに来るため、スタックトレースで発生箇所を特定できる。
            _logger.Error($"ジョブ失敗 [{job.VideoMetadata.Title}] <{job.VideoMetadata.Url}>", ex);
            job.Status = DownloadStatus.Failed;
            job.ErrorMessage = ex.Message;
            JobStatusChanged?.Invoke(this, new DownloadJobEventArgs(job));
        }
        finally
        {
            lock (_lock)
            {
                _cancellationTokens.Remove(job.Id);
            }
            cts.Dispose();
            if (semaphoreAcquired)
            {
                try
                {
                    _semaphore.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Dispose済みなら終了処理中なので何もしない
                }
            }
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
        lock (_lock)
        {
            var job = _allJobs.Find(j => j.Id == jobId);
            if (job != null && (job.Status == DownloadStatus.Failed || job.Status == DownloadStatus.Canceled))
            {
                job.Status = DownloadStatus.Pending;
                job.ErrorMessage = null;
                job.Progress = 0;
                JobStatusChanged?.Invoke(this, new DownloadJobEventArgs(job));
                _ = ProcessJobAsync(job);
            }
        }
    }

    public void ClearCompleted()
    {
        lock (_lock)
        {
            _allJobs.RemoveAll(j => j.Status == DownloadStatus.Completed || j.Status == DownloadStatus.Canceled);
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
        if (_disposed) return;
        _disposed = true;

        _settingsRepository.SettingsSaved -= OnSettingsSaved;

        lock (_lock)
        {
            foreach (var cts in _cancellationTokens.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _cancellationTokens.Clear();
        }

        _semaphore.Dispose();
    }
}
