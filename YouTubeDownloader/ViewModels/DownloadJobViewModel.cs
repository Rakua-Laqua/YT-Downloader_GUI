using System;
using System.IO;
using System.Text;
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
        DownloadStatus.CompletedWithWarning => $"完了（警告）: {ErrorMessage}",
        DownloadStatus.Failed => $"失敗: {ErrorMessage ?? "不明なエラー"}",
        DownloadStatus.Canceled => "キャンセル",
        _ => ""
    };

    public bool CanCancel => Status == DownloadStatus.Pending || Status == DownloadStatus.Running;
    public bool CanRetry => Status == DownloadStatus.Failed || Status == DownloadStatus.Canceled;

    /// <summary>フォーマット不一致警告があるか（完了時のみ表示対象）</summary>
    public bool HasFormatMismatch => IsCompleted(Status) && _job.HasFormatMismatch;

    /// <summary>フォーマット検証のツールチップ（不一致警告／音声変換情報）</summary>
    public string? FormatMismatchTooltip => _job.FormatMismatchTooltip;

    /// <summary>完了時にダウンロード情報を表示できるか</summary>
    public bool HasCompletionInfo => IsCompleted(Status);

    /// <summary>完了時の情報ツールチップ（所要時間・保存先・フォーマット検証など）</summary>
    public string? CompletionInfoTooltip => HasCompletionInfo ? BuildCompletionInfoTooltip() : null;

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
        OnPropertyChanged(nameof(HasCompletionInfo));
        OnPropertyChanged(nameof(CompletionInfoTooltip));
    }

    private static bool IsCompleted(DownloadStatus status) =>
        status is DownloadStatus.Completed or DownloadStatus.CompletedWithWarning;

    private string BuildCompletionInfoTooltip()
    {
        var sb = new StringBuilder();
        sb.AppendLine("ダウンロード情報");

        if (!string.IsNullOrWhiteSpace(_job.ErrorMessage))
        {
            sb.AppendLine();
            sb.AppendLine("警告");
            sb.AppendLine(_job.ErrorMessage);
            sb.AppendLine();
        }

        if (_job.StartedAt.HasValue && _job.CompletedAt.HasValue)
        {
            sb.AppendLine($"経過時間: {FormatElapsed(_job.CompletedAt.Value - _job.StartedAt.Value)}");
            sb.AppendLine($"開始: {_job.StartedAt.Value:yyyy/MM/dd HH:mm:ss}");
            sb.AppendLine($"完了: {_job.CompletedAt.Value:yyyy/MM/dd HH:mm:ss}");
        }
        else
        {
            sb.AppendLine("経過時間: 不明");
        }

        sb.AppendLine($"動画長: {_job.VideoMetadata.DurationFormatted}");
        sb.AppendLine($"要求: {ValueOrUnknown(_job.Format)} / {ValueOrUnknown(_job.Quality)}");
        sb.AppendLine($"サイズ: {GetFileSizeText(_job.VideoMetadata.LocalFilePath)}");
        sb.AppendLine($"保存先: {ValueOrUnknown(_job.VideoMetadata.LocalFilePath)}");
        sb.AppendLine();
        sb.AppendLine($"検証結果: {GetFormatVerificationText()}");
        sb.AppendLine($"yt-dlp選択: {BuildFormatSummary(_job.SourceExt, _job.SourceVcodec, _job.SourceAcodec)}");
        sb.AppendLine($"ffprobe実ファイル: {BuildFormatSummary(_job.ActualExt, _job.ActualVcodec, _job.ActualAcodec)}");

        if (!string.IsNullOrWhiteSpace(_job.FormatMismatchTooltip))
        {
            sb.AppendLine();
            sb.AppendLine(_job.FormatMismatchTooltip);
        }

        return sb.ToString().TrimEnd();
    }

    private string GetFormatVerificationText()
    {
        if (_job.HasFormatMismatch)
        {
            return "不一致あり";
        }

        var hasAnyFormatInfo =
            !string.IsNullOrWhiteSpace(_job.SourceExt)
            || !string.IsNullOrWhiteSpace(_job.SourceVcodec)
            || !string.IsNullOrWhiteSpace(_job.SourceAcodec)
            || !string.IsNullOrWhiteSpace(_job.ActualExt)
            || !string.IsNullOrWhiteSpace(_job.ActualVcodec)
            || !string.IsNullOrWhiteSpace(_job.ActualAcodec);

        return hasAnyFormatInfo ? "警告なし" : "情報不足";
    }

    private static string BuildFormatSummary(string? ext, string? vcodec, string? acodec)
    {
        return $"ext={ValueOrUnknown(ext)} / vcodec={ValueOrUnknown(vcodec)} / acodec={ValueOrUnknown(acodec)}";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            return "不明";
        }

        if (elapsed.TotalHours >= 1)
        {
            return $"{(int)elapsed.TotalHours}時間 {elapsed.Minutes}分 {elapsed.Seconds}秒";
        }

        if (elapsed.TotalMinutes >= 1)
        {
            return $"{elapsed.Minutes}分 {elapsed.Seconds}秒";
        }

        return elapsed.TotalSeconds < 1
            ? "1秒未満"
            : $"{elapsed.Seconds}秒";
    }

    private static string GetFileSizeText(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "不明";
        }

        try
        {
            if (!File.Exists(path))
            {
                return "不明";
            }

            var bytes = new FileInfo(path).Length;
            return FormatBytes(bytes);
        }
        catch
        {
            return "不明";
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes} {units[unitIndex]}"
            : $"{value:0.0} {units[unitIndex]}";
    }

    private static string ValueOrUnknown(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "不明" : value.Trim();
    }
}
