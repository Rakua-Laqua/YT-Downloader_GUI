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
        if (!TryCreateBrowserUri(url, out var uri))
        {
            MessageBox.Show("開けるURLは http または https のみです。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            $"YouTubeを既定ブラウザで開きますか？\n\n{title}\n{uri.AbsoluteUri}",
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
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ブラウザを開けませんでした: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool TryCreateBrowserUri(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}
