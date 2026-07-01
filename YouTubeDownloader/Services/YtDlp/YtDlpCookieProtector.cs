using System;
using System.Collections.Generic;
using System.IO;

namespace YouTubeDownloader.Services;

/// <summary>
/// yt-dlp は <c>--cookies FILE</c> で渡したファイルへ、実行後にローテーション結果を書き戻す。
/// ユーザーの cookies.txt を直接渡すと、サーバ側でローテーション/失効したcookieがそのまま書き戻され、
/// SID等の認証cookieが削られて以後ログインできなくなることがある（特にブラウザに通常ログイン中だと顕著）。
/// そこで実行ごとに一時コピーを作ってそちらを渡し、書き戻しはコピー側に行わせて破棄することで、
/// ユーザーの元ファイルを常にエクスポート直後の状態のまま保護する。
/// </summary>
internal static class YtDlpCookieProtector
{
    /// <summary>
    /// 引数内の "--cookies &lt;path&gt;" を一時コピーへ差し替えたスコープを返す。
    /// using で受け、Dispose（=プロセス終了後）に一時ファイルを削除する。
    /// cookie未指定・ファイル不在・コピー失敗時は元の引数のまま（後始末も不要）。
    /// </summary>
    public static CookieCopyScope Begin(IEnumerable<string> arguments, ILoggingService? logger = null)
    {
        var args = new List<string>(arguments);
        var index = args.IndexOf("--cookies");
        if (index < 0 || index + 1 >= args.Count)
        {
            return new CookieCopyScope(args, null);
        }

        var masterPath = args[index + 1];
        if (string.IsNullOrEmpty(masterPath) || !File.Exists(masterPath))
        {
            return new CookieCopyScope(args, null);
        }

        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"ytdlp_cookies_{Guid.NewGuid():N}.txt");
            File.Copy(masterPath, tempPath, overwrite: true);
            args[index + 1] = tempPath;
            return new CookieCopyScope(args, tempPath);
        }
        catch (Exception ex)
        {
            // コピーに失敗しても元のパスのまま実行する（最悪でも従来挙動）
            logger?.Warn($"cookieファイルの一時コピー作成に失敗しました。元ファイルを直接参照して実行します: {ex.GetType().Name}: {ex.Message}");
            return new CookieCopyScope(args, null);
        }
    }

    internal sealed class CookieCopyScope : IDisposable
    {
        /// <summary>yt-dlpへ渡す実効引数（cookieは一時コピーに差し替え済み）。</summary>
        public List<string> Arguments { get; }

        private readonly string? _tempFile;

        public CookieCopyScope(List<string> arguments, string? tempFile)
        {
            Arguments = arguments;
            _tempFile = tempFile;
        }

        public void Dispose()
        {
            if (string.IsNullOrEmpty(_tempFile))
            {
                return;
            }

            try
            {
                if (File.Exists(_tempFile))
                {
                    File.Delete(_tempFile);
                }
            }
            catch
            {
                // 一時ファイルの削除失敗は無視（次回起動時のTempクリーンアップに任せる）
            }
        }
    }
}
