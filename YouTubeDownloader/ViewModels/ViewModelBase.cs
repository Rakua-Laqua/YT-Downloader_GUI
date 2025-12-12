using CommunityToolkit.Mvvm.ComponentModel;

namespace YouTubeDownloader.ViewModels;

/// <summary>
/// ViewModelの基底クラス
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    private string? _busyMessage;
    public string? BusyMessage
    {
        get => _busyMessage;
        set => SetProperty(ref _busyMessage, value);
    }
}
