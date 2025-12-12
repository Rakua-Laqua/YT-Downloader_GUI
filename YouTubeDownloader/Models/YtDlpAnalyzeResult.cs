namespace YouTubeDownloader.Models;

/// <summary>
/// yt-dlp解析結果を表すモデル
/// </summary>
public class YtDlpAnalyzeResult
{
    /// <summary>解析成功フラグ</summary>
    public bool IsSuccess { get; set; }

    /// <summary>プレイリストかどうか</summary>
    public bool IsPlaylist { get; set; }

    /// <summary>単一動画メタデータ（プレイリストでない場合）</summary>
    public VideoMetadata? VideoMetadata { get; set; }

    /// <summary>プレイリストメタデータ（プレイリストの場合）</summary>
    public PlaylistMetadata? PlaylistMetadata { get; set; }

    /// <summary>エラーメッセージ</summary>
    public string? ErrorMessage { get; set; }
}
