using System.Collections.Generic;

namespace YouTubeDownloader.Models;

/// <summary>
/// プレイリストのメタデータを表すモデル
/// </summary>
public class PlaylistMetadata
{
    /// <summary>プレイリストID</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>プレイリストタイトル</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>チャンネル名</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>動画数（実際に取得できた数）</summary>
    public int VideoCount { get; set; }

    /// <summary>
    /// YouTube上のプレイリスト総数（yt-dlpの playlist_count）。
    /// 取得できなかった場合は実取得数(<see cref="VideoCount"/>)と一致させる。
    /// </summary>
    public int TotalVideoCount { get; set; }

    /// <summary>
    /// 取得できた件数がYouTube上の総数より少ない（＝取得漏れがある）かどうか。
    /// 主に古いyt-dlpのプレイリスト・ページネーション不具合の検知に使う。
    /// </summary>
    public bool IsTruncated => TotalVideoCount > VideoCount;

    /// <summary>ローカル保存フォルダパス</summary>
    public string LocalFolderPath { get; set; } = string.Empty;

    /// <summary>サムネイルURL</summary>
    public string ThumbnailUrl { get; set; } = string.Empty;

    /// <summary>プレイリスト内の動画一覧</summary>
    public List<VideoMetadata> Videos { get; set; } = new();
}
