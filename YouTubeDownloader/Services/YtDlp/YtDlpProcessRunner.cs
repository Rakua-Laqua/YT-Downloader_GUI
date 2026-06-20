using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YouTubeDownloader.Services;

internal static class YtDlpProcessRunner
{
    /// <summary>
    /// yt-dlpの標準エラー出力をデコードするエンコーディング。
    /// yt-dlpはリダイレクト時、出力をシステムのANSIコードページ（日本語ならcp932）で書き出すため、
    /// UTF-8でデコードすると日本語のエラーメッセージが文字化けする。ANSIコードページで読む。
    /// </summary>
    private static readonly Encoding StdErrEncoding = ResolveStdErrEncoding();

    public static async Task<string> RunAsync(string ytDlpPath, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var (stdout, _, _) = await RunRawAsync(ytDlpPath, arguments, cancellationToken);
        return stdout;
    }

    /// <summary>
    /// yt-dlpを実行し、標準出力・標準エラー・終了コードをまとめて返す。
    /// stderrはエラー理由の表示に使うため、ANSIコードページでデコードして文字化けを防ぐ。
    /// </summary>
    public static async Task<(string StdOut, string StdErr, int ExitCode)> RunRawAsync(
        string ytDlpPath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        // cookieは元ファイルを汚さないよう一時コピーに差し替えて渡す（using でプロセス終了後に破棄）
        using var cookieScope = YtDlpCookieProtector.Begin(arguments);
        var psi = CreateStartInfo(ytDlpPath, cookieScope.Arguments);

        using var process = new Process { StartInfo = psi };
        process.Start();
        using var killRegistration = RegisterProcessKillOnCancellation(process, cancellationToken);

        // stderrも同時に読み取る。リダイレクトしたまま読まないと、
        // 警告などでパイプバッファが満杯になった時点でyt-dlpがブロックしハングする。
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await Task.WhenAll(outputTask, errorTask);
        await process.WaitForExitAsync(cancellationToken);

        return (outputTask.Result, errorTask.Result, process.ExitCode);
    }

    internal static ProcessStartInfo CreateStartInfo(string ytDlpPath, IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = StdErrEncoding
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        return psi;
    }

    internal static CancellationTokenRegistration RegisterProcessKillOnCancellation(Process process, CancellationToken cancellationToken)
    {
        return cancellationToken.Register(() => KillProcessTree(process));
    }

    private static Encoding ResolveStdErrEncoding()
    {
        try
        {
            // .NET (Core)では既定でcp932等のコードページが使えないため、プロバイダを登録する
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var ansiCodePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            if (ansiCodePage > 0)
            {
                return Encoding.GetEncoding(ansiCodePage);
            }
        }
        catch
        {
            // 取得失敗時はUTF-8にフォールバック
        }
        return Encoding.UTF8;
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may already be gone.
        }
    }
}
