using CommunityToolkit.Mvvm.ComponentModel;
using YouTubeDownloader.Models;

namespace YouTubeDownloader.ViewModels;

/// <summary>
/// プレイリストアイテムのViewModel
/// </summary>
public partial class PlaylistItemViewModel : ObservableObject
{
    public VideoMetadata Video { get; }

    public PlaylistItemViewModel(VideoMetadata video)
    {
        Video = video;
    }

    [ObservableProperty]
    private bool _isSelected = true;

    public int Index => Video.PlaylistIndex ?? 0;
    public string Title => Video.Title;
    public string Channel => Video.Channel;
    public string Duration => Video.DurationFormatted;
    public string ThumbnailUrl => Video.ThumbnailUrl;
}
