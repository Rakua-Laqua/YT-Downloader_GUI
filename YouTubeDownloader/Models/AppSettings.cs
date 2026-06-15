using System;
using System.IO;

namespace YouTubeDownloader.Models;

/// <summary>
/// アプリケーション設定を表すモデル
/// </summary>
public class AppSettings
{
    /// <summary>yt-dlp実行ファイルパス</summary>
    public string YtDlpPath { get; set; } = string.Empty;

    /// <summary>ffmpeg実行ファイルパス</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>yt-dlpを初回利用前に自動更新するか</summary>
    public bool AutoUpdateYtDlp { get; set; } = true;

    /// <summary>
    /// yt-dlpの更新チャンネル（"stable" または "nightly"）。
    /// プレイリスト取得などの不具合がstableで未修正の場合に nightly を選べるようにする。
    /// </summary>
    public string YtDlpUpdateChannel { get; set; } = "stable";

    /// <summary>動画タイトルなどのメタデータ取得に使う既定言語</summary>
    public string DefaultMetadataLanguage { get; set; } = "default";

    /// <summary>デフォルト保存先フォルダ</summary>
    public string DefaultSaveFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "YouTubeDownloader");

    /// <summary>デフォルト動画フォーマット</summary>
    public string DefaultVideoFormat { get; set; } = "mp4";

    /// <summary>デフォルト音声フォーマット</summary>
    public string DefaultAudioFormat { get; set; } = "mp3";

    /// <summary>デフォルト品質</summary>
    public string DefaultQuality { get; set; } = "best";

    /// <summary>デフォルト音声品質</summary>
    public string DefaultAudioQuality { get; set; } = "標準 (VBR 5)";

    /// <summary>mp4でもAV1などの高効率コーデックを優先するか</summary>
    public bool PreferHighEfficiencyCodecs { get; set; }

    /// <summary>ファイルの更新日時を動画の公開日に合わせるか</summary>
    public bool SetFileDateToPublishDate { get; set; }

    /// <summary>メタデータの「年」タグを動画の公開年に修正するか</summary>
    public bool FixMetadataYear { get; set; }

    /// <summary>ファイル名テンプレート</summary>
    public string FilenameTemplate { get; set; } = "{title}";

    /// <summary>同時ダウンロード数</summary>
    public int MaxConcurrentDownloads { get; set; } = 2;
}
