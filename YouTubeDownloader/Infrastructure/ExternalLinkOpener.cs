using System;
using System.Diagnostics;
using System.Windows;

namespace YouTubeDownloader.Infrastructure;

/// <summary>
/// 動画URLを確認ダイアログ付きで既定ブラウザで開く（ダウンロード画面とライブラリ画面で共通）
/// </summary>
internal static class ExternalLinkOpener
{
    public static void ConfirmAndOpenVideoLink(string? url, string? title)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show("動画URLがありません。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            $"YouTubeを既定ブラウザで開きますか？\n\n{title}\n{url}",
            "確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ブラウザを開けませんでした: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
