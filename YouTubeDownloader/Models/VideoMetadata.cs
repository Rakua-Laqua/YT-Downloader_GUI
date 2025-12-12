using System;

namespace YouTubeDownloader.Models;

/// <summary>
/// 動画のメタデータを表すモデル
/// </summary>
public class VideoMetadata
{
    /// <summary>動画ID</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>動画タイトル</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>チャンネル名</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>再生時間（秒）</summary>
    public int DurationSeconds { get; set; }

    /// <summary>公開日</summary>
    public DateTime? PublishDate { get; set; }

    /// <summary>サムネイルURL</summary>
    public string ThumbnailUrl { get; set; } = string.Empty;

    /// <summary>ローカル保存パス</summary>
    public string LocalFilePath { get; set; } = string.Empty;

    /// <summary>プレイリストID（プレイリストの一部の場合）</summary>
    public string? PlaylistId { get; set; }

    /// <summary>プレイリスト内のインデックス</summary>
    public int? PlaylistIndex { get; set; }

    /// <summary>ダウンロード完了日時</summary>
    public DateTime DownloadedAt { get; set; }

    /// <summary>ダウンロードしたフォーマット</summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>動画URL</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>再生時間を文字列でフォーマット</summary>
    public string DurationFormatted
    {
        get
        {
            var ts = TimeSpan.FromSeconds(DurationSeconds);
            return ts.Hours > 0
                ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes}:{ts.Seconds:D2}";
        }
    }
}
