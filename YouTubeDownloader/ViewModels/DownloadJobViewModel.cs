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
    public string Url => _job.VideoMetadata.Url;

    [ObservableProperty]
    private DownloadStatus _status;

    [ObservableProperty]
    private int _progress;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isPostProcessing;

    [ObservableProperty]
    private string? _errorMessage;

    public string StatusText => Status switch
    {
        DownloadStatus.Pending => "待機中",
        // 後処理(変換・埋め込み)はパーセンテージが取れないので「%」を出さない
        DownloadStatus.Running when IsPostProcessing => StatusMessage ?? "変換中...",
        DownloadStatus.Running => !string.IsNullOrWhiteSpace(StatusMessage)
            ? $"{StatusMessage} ({Progress}%)"
            : $"ダウンロード中 ({Progress}%)",
        DownloadStatus.Completed => "完了",
        DownloadStatus.Failed => $"失敗: {ErrorMessage ?? "不明なエラー"}",
        DownloadStatus.Canceled => "キャンセル",
        _ => ""
    };

    public bool CanCancel => Status == DownloadStatus.Pending || Status == DownloadStatus.Running;
    public bool CanRetry => Status == DownloadStatus.Failed || Status == DownloadStatus.Canceled;

    /// <summary>フォーマット不一致警告があるか（完了時のみ表示対象）</summary>
    public bool HasFormatMismatch => Status == DownloadStatus.Completed && _job.HasFormatMismatch;

    /// <summary>フォーマット検証のツールチップ（不一致警告／音声変換情報）</summary>
    public string? FormatMismatchTooltip => _job.FormatMismatchTooltip;

    /// <summary>失敗時の詳細（フェーズ・終了コード・stderr全文など）。ログと同一内容。</summary>
    public string? FailureDetail => _job.FailureDetail;

    /// <summary>「失敗詳細をコピー」ボタンを出すか</summary>
    public bool HasFailureDetail => Status == DownloadStatus.Failed && !string.IsNullOrWhiteSpace(_job.FailureDetail);

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

    [RelayCommand]
    private void CopyFailureDetail()
    {
        if (string.IsNullOrWhiteSpace(_job.FailureDetail))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(_job.FailureDetail);
        }
        catch
        {
            // クリップボードが他プロセスにロックされている場合などは無視する
        }
    }

    public void UpdateFromJob()
    {
        Status = _job.Status;
        Progress = _job.Progress;
        StatusMessage = _job.StatusMessage;
        IsPostProcessing = _job.IsPostProcessing;
        ErrorMessage = _job.ErrorMessage;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(FailureDetail));
        OnPropertyChanged(nameof(HasFailureDetail));
        OnPropertyChanged(nameof(HasFormatMismatch));
        OnPropertyChanged(nameof(FormatMismatchTooltip));
    }
}
