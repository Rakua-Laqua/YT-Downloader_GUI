using System;

namespace YouTubeDownloader.Models;

/// <summary>
/// ダウンロードジョブの状態
/// </summary>
public enum DownloadStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Canceled
}

/// <summary>
/// ダウンロード対象の種別
/// </summary>
public enum DownloadTargetType
{
    SingleVideo,
    PlaylistItem
}

/// <summary>
/// ダウンロードジョブを表すモデル
/// </summary>
public class DownloadJob
{
    /// <summary>ジョブID</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>ダウンロード対象種別</summary>
    public DownloadTargetType TargetType { get; set; }

    /// <summary>動画メタデータ</summary>
    public VideoMetadata VideoMetadata { get; set; } = new();

    /// <summary>保存先フォルダパス</summary>
    public string SaveFolderPath { get; set; } = string.Empty;

    /// <summary>フォーマット（mp4, mp3など）</summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>品質（best, 1080p, 720pなど）</summary>
    public string Quality { get; set; } = string.Empty;

    /// <summary>ダウンロード状態</summary>
    public DownloadStatus Status { get; set; } = DownloadStatus.Pending;

    /// <summary>進捗（0-100）</summary>
    public int Progress { get; set; }

    /// <summary>実行中の詳細ステータス</summary>
    public string? StatusMessage { get; set; }

    /// <summary>変換・埋め込みなどの後処理フェーズ中か（進捗バーを不確定表示にするため）</summary>
    public bool IsPostProcessing { get; set; }

    /// <summary>エラーメッセージ</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>ダウンロード開始時刻</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>ダウンロード完了時刻</summary>
    public DateTime? CompletedAt { get; set; }
}
