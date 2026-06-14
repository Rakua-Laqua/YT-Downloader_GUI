using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    private readonly ConcurrentQueue<DownloadJob> _pendingQueue = new();
    private readonly List<DownloadJob> _allJobs = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _cancellationTokens = new();
    private readonly SemaphoreSlim _semaphore;
    private readonly object _lock = new();
    private const int MaxConcurrency = 2;
    private bool _disposed;

    public event EventHandler<DownloadJobEventArgs>? JobProgressChanged;
    public event EventHandler<DownloadJobEventArgs>? JobStatusChanged;

    public DownloadManager(IYtDlpClient ytDlpClient, IMetadataRepository metadataRepository)
    {
        _ytDlpClient = ytDlpClient;
        _metadataRepository = metadataRepository;
        _semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
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

        try
        {
            await _semaphore.WaitAsync(cts.Token);
            semaphoreAcquired = true;
            cts.Token.ThrowIfCancellationRequested();

            job.Status = DownloadStatus.Running;
            job.StartedAt = DateTime.Now;
            job.Progress = 0;
            job.IsPostProcessing = false;
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
            var shouldNotify = job.Status != DownloadStatus.Canceled;
            job.Status = DownloadStatus.Canceled;
            if (shouldNotify)
            {
                JobStatusChanged?.Invoke(this, new DownloadJobEventArgs(job));
            }
        }
        catch (Exception ex)
        {
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
                _semaphore.Release();
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
