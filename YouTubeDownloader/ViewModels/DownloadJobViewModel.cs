using CommunityToolkit.Mvvm.ComponentModel;
using YouTubeDownloader.Models;

namespace YouTubeDownloader.ViewModels;

/// <summary>
/// ダウンロードジョブのViewModel
/// </summary>
public partial class DownloadJobViewModel : ObservableObject
{
    private readonly DownloadJob _job;

    public DownloadJobViewModel(DownloadJob job)
    {
        _job = job;
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
        DownloadStatus.Failed => "失敗",
        DownloadStatus.Canceled => "キャンセル",
        _ => ""
    };

    public bool CanCancel => Status == DownloadStatus.Pending || Status == DownloadStatus.Running;
    public bool CanRetry => Status == DownloadStatus.Failed || Status == DownloadStatus.Canceled;

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
