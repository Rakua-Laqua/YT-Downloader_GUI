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

    /// <summary>動画数</summary>
    public int VideoCount { get; set; }

    /// <summary>ローカル保存フォルダパス</summary>
    public string LocalFolderPath { get; set; } = string.Empty;

    /// <summary>サムネイルURL</summary>
    public string ThumbnailUrl { get; set; } = string.Empty;

    /// <summary>プレイリスト内の動画一覧</summary>
    public List<VideoMetadata> Videos { get; set; } = new();
}
