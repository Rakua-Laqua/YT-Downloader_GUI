using System;
using System.IO;

namespace YouTubeDownloader.Services;

/// <summary>
/// yt-dlp / ffmpeg などの実行ファイルをPATHと一般的なインストール先から検索するヘルパー。
/// YtDlpClientと設定画面の自動検出で同じ探索順を共有する。
/// </summary>
internal static class ExecutableLocator
{
    public static string? FindExecutable(string windowsName, string unixName)
    {
        var exeName = Environment.OSVersion.Platform == PlatformID.Win32NT ? windowsName : unixName;

        // 1. PATH環境変数から検索
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var path in pathEnv.Split(Path.PathSeparator))
            {
                var fullPath = Path.Combine(path, exeName);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        // 2. 一般的なインストール場所を検索 (Windows)
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            var baseName = exeName.Replace(".exe", "");
            var commonPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", exeName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), baseName, exeName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), baseName, exeName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", exeName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Programs", baseName, exeName),
                Path.Combine("C:\\", baseName, exeName),
                Path.Combine("C:\\tools", exeName),
            };

            foreach (var path in commonPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        // 3. アプリケーションディレクトリ
        var appPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, exeName);
        if (File.Exists(appPath))
        {
            return appPath;
        }

        return null;
    }
}
