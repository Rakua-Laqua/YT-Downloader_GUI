using System;
using CommunityToolkit.Mvvm.ComponentModel;
using YouTubeDownloader.Models;

namespace YouTubeDownloader.ViewModels;

/// <summary>
/// ライブラリ表示用の動画メタデータViewModel
/// </summary>
public partial class VideoMetadataViewModel : ObservableObject
{
    public VideoMetadata Metadata { get; }

    public VideoMetadataViewModel(VideoMetadata metadata)
    {
        Metadata = metadata;
    }

    /// <summary>一括選択用のチェック状態</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>選択状態が変化したことを親(LibraryViewModel)へ通知する</summary>
    public event EventHandler? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this, EventArgs.Empty);

    public string Title => Metadata.Title;
    public string Channel => Metadata.Channel;
    public string Duration => Metadata.DurationFormatted;
    public string ThumbnailUrl => Metadata.ThumbnailUrl;
    public string LocalFilePath => Metadata.LocalFilePath;
    public string Format => Metadata.Format;
    public string DownloadedAt => Metadata.DownloadedAt.ToString("yyyy/MM/dd HH:mm");
    public string Url => Metadata.Url;
}
