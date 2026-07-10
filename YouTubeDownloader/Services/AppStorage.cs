using System;
using System.IO;

namespace YouTubeDownloader.Services;

internal static class AppStorage
{
    public static string GetAppFilePath(string fileName)
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appFolder = Path.Combine(appDataPath, "YouTubeDownloader");
        Directory.CreateDirectory(appFolder);
        return Path.Combine(appFolder, fileName);
    }

    public static void TryCopyUnreadableFile(string path, string displayName, ILoggingService logger)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var backupPath = $"{path}.invalid-{DateTime.Now:yyyyMMddHHmmss}";
            File.Copy(path, backupPath, overwrite: false);
            logger.Warn($"読み込めなかった{displayName}ファイルを退避しました: {backupPath}");
        }
        catch (Exception ex)
        {
            logger.Warn($"読み込めなかった{displayName}ファイルの退避に失敗しました: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
