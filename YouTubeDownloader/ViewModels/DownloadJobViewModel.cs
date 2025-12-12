using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouTubeDownloader.Models;

namespace YouTubeDownloader.ViewModels;

/// <summary>
/// ダウンロードジョブのViewModel
/// </summary>
public partial class DownloadJobViewModel : ObservableObject
{
    private readonly DownloadJob _job;
    private readonly Action<Guid>? _cancelAction;
    private readonly Action<Guid>? _retryAction;

    public DownloadJobViewModel(DownloadJob job, Action<Guid>? cancelAction = null, Action<Guid>? retryAction = null)
    {
        _job = job;
        _cancelAction = cancelAction;
        _retryAction = retryAction;
    }

    public DownloadJob Job => _job;
    public string Title => _job.VideoMetadata.Title;
    public string Channel => _job.VideoMetadata.Channel;
    public string ThumbnailUrl => _job.VideoMetadata.ThumbnailUrl;

    [ObservableProperty]
    private DownloadStatus _status;

    [ObservableProperty]
    private int _progress;

    [ObservableProperty]
    private string? _errorMessage;

    public string StatusText => Status switch
    {
        DownloadStatus.Pending => "待機中",
        DownloadStatus.Running => $"ダウンロード中 ({Progress}%)",
        DownloadStatus.Completed => "完了",
        DownloadStatus.Failed => $"失敗: {ErrorMessage ?? "不明なエラー"}",
        DownloadStatus.Canceled => "キャンセル",
        _ => ""
    };

    public bool CanCancel => Status == DownloadStatus.Pending || Status == DownloadStatus.Running;
    public bool CanRetry => Status == DownloadStatus.Failed || Status == DownloadStatus.Canceled;

    [RelayCommand]
    private void Cancel()
    {
        _cancelAction?.Invoke(_job.Id);
    }

    [RelayCommand]
    private void Retry()
    {
        _retryAction?.Invoke(_job.Id);
    }

    public void UpdateFromJob()
    {
        Status = _job.Status;
        Progress = _job.Progress;
        ErrorMessage = _job.ErrorMessage;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
    }
}
