using Microsoft.Win32;

namespace YouTubeDownloader.Infrastructure;

internal static class DialogPicker
{
    public static string? BrowseFile(string title, string filter, string fileName)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = fileName
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public static string? BrowseFolder(string title, string initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = initialDirectory
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
