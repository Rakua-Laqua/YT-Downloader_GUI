using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace YouTubeDownloader.Infrastructure;

/// <summary>
/// サムネイルURL(string)を、表示サイズに合わせて縮小デコードした BitmapImage へ変換する。
/// ・DecodePixelWidth で小さくデコードし、フル解像度画像の展開によるCPU/メモリ消費を抑える
/// ・URLごとに生成済みの(凍結された)BitmapImageをキャッシュし、仮想化スクロールでの再デコードを避ける
/// これによりライブラリ一覧の描画・スクロールが大幅に軽くなる。
/// </summary>
public class ThumbnailImageConverter : IValueConverter
{
    // 一覧サムネは 70x40 表示。高DPI/くっきり表示のため少し余裕を持たせた幅でデコードする。
    private const int DecodePixelWidth = 160;

    private static readonly Dictionary<string, BitmapImage> Cache = new();
    private static readonly object SyncRoot = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        lock (SyncRoot)
        {
            if (Cache.TryGetValue(url, out var cached))
            {
                return cached;
            }
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            // 読み込み完了後にデコードし、可能なら凍結してクロススレッド/再利用を可能にする
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = DecodePixelWidth;
            bitmap.UriSource = new Uri(url, UriKind.Absolute);
            bitmap.EndInit();

            // リモート画像はダウンロード中だと CanFreeze=false になるため、可能な場合のみ凍結する
            if (bitmap.CanFreeze)
            {
                bitmap.Freeze();
            }

            lock (SyncRoot)
            {
                Cache[url] = bitmap;
            }

            return bitmap;
        }
        catch
        {
            // 不正URL等は画像なしとして扱う
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
