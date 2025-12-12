using CommunityToolkit.Mvvm.ComponentModel;
using YouTubeDownloader.Models;

namespace YouTubeDownloader.ViewModels;

/// <summary>
/// ライブラリ表示用の動画メタデータViewModel
/// </summary>
public class VideoMetadataViewModel : ObservableObject
{
    public VideoMetadata Metadata { get; }

    public VideoMetadataViewModel(VideoMetadata metadata)
    {
        Metadata = metadata;
    }

    public string Title => Metadata.Title;
    public string Channel => Metadata.Channel;
    public string Duration => Metadata.DurationFormatted;
    public string ThumbnailUrl => Metadata.ThumbnailUrl;
    public string LocalFilePath => Metadata.LocalFilePath;
    public string Format => Metadata.Format;
    public string DownloadedAt => Metadata.DownloadedAt.ToString("yyyy/MM/dd HH:mm");
    public string Url => Metadata.Url;
}
