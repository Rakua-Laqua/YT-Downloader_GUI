using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using YouTubeDownloader.Models;

namespace YouTubeDownloader.Services;

internal static class YtDlpFailureFormatter
{
    public static string FormatArgumentsForLog(IEnumerable<string> arguments)
    {
        return string.Join(" ", arguments.Select(QuoteArgumentForLog));
    }

    /// <summary>
    /// yt-dlpの標準エラー出力から、表示に適した「意味のある」行を抜き出す。
    /// WARNINGなどのノイズを除き、ERROR行を優先して返す。
    /// </summary>
    public static string ExtractMeaningfulError(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return "原因不明のエラーが発生しました。";
        }

        var lines = stderr
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        var errorLines = lines
            .Where(line => line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var chosen = errorLines.Count > 0 ? errorLines : lines.TakeLast(3).ToList();
        return string.Join(Environment.NewLine, chosen);
    }

    public static string ExtractMeaningfulError(YtDlpRunResult run)
    {
        var stderrReason = ExtractMeaningfulError(run.StdErr);
        if (stderrReason != "原因不明のエラーが発生しました。")
        {
            return stderrReason;
        }

        if (string.IsNullOrWhiteSpace(run.StdOutDiagnostics))
        {
            return stderrReason;
        }

        var lines = run.StdOutDiagnostics
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .TakeLast(6);

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 失敗したyt-dlp実行から、利用者向けの理由テキストを組み立てる。
    /// 既知のエラーパターンなら正確な「原因」と「対処」を先頭に付け、続けて生のエラー文を残す。
    /// 未知のパターンなら従来どおり生のエラー文だけを返す。
    /// </summary>
    public static string DescribeError(YtDlpRunResult run)
    {
        return Compose(CombineForClassification(run), ExtractMeaningfulError(run));
    }

    public static string DescribeError(string stderr)
    {
        return Compose(stderr, ExtractMeaningfulError(stderr));
    }

    /// <summary>
    /// 初回失敗が、別のplayer clientでの再試行で回復し得るかを判定する。
    /// bot検知・年齢制限・非公開・429 など回復不能なものは false（＝再試行せず即失敗にしてよい）。
    /// </summary>
    public static bool ShouldRetryWithFallbackClient(YtDlpRunResult run)
    {
        return YtDlpErrorClassifier.IsRetryWorthwhile(CombineForClassification(run));
    }

    /// <summary>
    /// 初回・再試行の両方が失敗したときの、利用者向け理由テキストを組み立てる。
    /// 既知パターンの正確な説明を先頭に、続けて各試行の生エラーを並べる。
    /// </summary>
    public static string BuildUserFacingDownloadReason(YtDlpRunResult firstRun, YtDlpRunResult retryRun)
    {
        var firstReason = ExtractMeaningfulError(firstRun);
        var retryReason = ExtractMeaningfulError(retryRun);
        var diagnosis = YtDlpErrorClassifier.Classify(CombineForClassification(retryRun))
                        ?? YtDlpErrorClassifier.Classify(CombineForClassification(firstRun));

        var sb = new StringBuilder();
        if (diagnosis != null)
        {
            AppendDiagnosis(sb, diagnosis);
            sb.AppendLine();
        }
        sb.AppendLine("【初回試行のエラー】:");
        sb.AppendLine(firstReason);
        sb.AppendLine();
        sb.AppendLine("【再試行（フォールバック）のエラー】:");
        sb.Append(retryReason);
        return sb.ToString();
    }

    /// <summary>ログ行に付けるジョブ識別ラベル（タイトルとURL）を作る</summary>
    public static string BuildJobLabel(DownloadJob job)
    {
        var title = string.IsNullOrWhiteSpace(job.VideoMetadata.Title) ? "(無題)" : job.VideoMetadata.Title;
        var url = string.IsNullOrWhiteSpace(job.VideoMetadata.Url) ? "(URL未設定)" : job.VideoMetadata.Url;
        return $"[{title}] <{url}>";
    }

    /// <summary>準備段階(URL検証・実行ファイル検出・出力パス構築)の失敗詳細を組み立てる</summary>
    public static string BuildPreflightFailureDetail(DownloadJob job, string phase, string reason, Exception? exception)
    {
        var sb = new StringBuilder();
        AppendFailureHeader(sb, job);
        sb.AppendLine($"失敗フェーズ: {phase}");
        sb.AppendLine($"理由: {reason}");
        if (exception != null)
        {
            sb.AppendLine($"例外: {exception.GetType().FullName}: {exception.Message}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// yt-dlp実行が初回・再試行ともに失敗したときの詳細を組み立てる。
    /// 「どのタイミングで・なぜ」を後から追えるよう、両試行の終了コード・失敗フェーズ・経過時間・
    /// 実行コマンド・stderr全文と、yt-dlpバージョンをまとめる。
    /// </summary>
    public static string BuildDownloadFailureDetail(
        DownloadJob job,
        string ytDlpVersion,
        IReadOnlyList<string> firstArguments,
        IReadOnlyList<string> retryArguments,
        YtDlpRunResult firstRun,
        YtDlpRunResult retryRun)
    {
        var sb = new StringBuilder();
        AppendFailureHeader(sb, job);
        sb.AppendLine($"yt-dlp バージョン: {ytDlpVersion}");

        // 既知のエラーパターンなら、両試行の出力から推定した原因・対処を先頭にまとめる
        var diagnosis = YtDlpErrorClassifier.Classify(CombineForClassification(retryRun))
                        ?? YtDlpErrorClassifier.Classify(CombineForClassification(firstRun));
        if (diagnosis != null)
        {
            sb.AppendLine();
            sb.AppendLine("---- 推定原因 ----");
            AppendDiagnosis(sb, diagnosis);
        }

        sb.AppendLine();
        AppendAttemptDetail(sb, "初回試行", firstArguments, firstRun);
        sb.AppendLine();
        AppendAttemptDetail(sb, "再試行(フォールバックclient)", retryArguments, retryRun);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 再試行せず単一試行で失敗が確定したとき（回復不能と判断した場合）の詳細を組み立てる。
    /// </summary>
    public static string BuildDownloadFailureDetail(
        DownloadJob job,
        string ytDlpVersion,
        IReadOnlyList<string> arguments,
        YtDlpRunResult run)
    {
        var sb = new StringBuilder();
        AppendFailureHeader(sb, job);
        sb.AppendLine($"yt-dlp バージョン: {ytDlpVersion}");

        var diagnosis = YtDlpErrorClassifier.Classify(CombineForClassification(run));
        if (diagnosis != null)
        {
            sb.AppendLine();
            sb.AppendLine("---- 推定原因 ----");
            AppendDiagnosis(sb, diagnosis);
            sb.AppendLine();
            sb.AppendLine("（このエラーは別clientで再試行しても回復しないため、再試行は行いませんでした）");
        }

        sb.AppendLine();
        AppendAttemptDetail(sb, "実行", arguments, run);
        return sb.ToString().TrimEnd();
    }

    public static string BuildAttemptDiagnosticDetail(string label, IReadOnlyList<string> arguments, YtDlpRunResult run)
    {
        var sb = new StringBuilder();
        AppendAttemptDetail(sb, label, arguments, run);
        return sb.ToString().TrimEnd();
    }

    /// <summary>分類の判定材料。stderrに加え、429などWARNINGとして出る情報も拾えるよう診断出力も連結する。</summary>
    private static string CombineForClassification(YtDlpRunResult run)
    {
        return run.StdErr + "\n" + run.StdOutDiagnostics;
    }

    /// <summary>分類結果（あれば）を先頭に、続けて生のエラー文を付けたテキストを返す。</summary>
    private static string Compose(string classificationSource, string rawReason)
    {
        var diagnosis = YtDlpErrorClassifier.Classify(classificationSource);
        if (diagnosis == null)
        {
            return rawReason;
        }

        var sb = new StringBuilder();
        AppendDiagnosis(sb, diagnosis);
        sb.AppendLine();
        sb.Append($"yt-dlp: {rawReason}");
        return sb.ToString();
    }

    private static void AppendDiagnosis(StringBuilder sb, YtDlpErrorClassifier.Diagnosis diagnosis)
    {
        sb.AppendLine($"■ {diagnosis.Category}");
        sb.AppendLine($"原因: {diagnosis.Summary}");
        sb.AppendLine($"対処: {diagnosis.Advice}");
    }

    private static string QuoteArgumentForLog(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        return argument.Any(char.IsWhiteSpace) || argument.Contains('"')
            ? $"\"{argument.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""
            : argument;
    }

    private static void AppendFailureHeader(StringBuilder sb, DownloadJob job)
    {
        sb.AppendLine("==== ダウンロード失敗詳細 ====");
        sb.AppendLine($"日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"タイトル: {job.VideoMetadata.Title}");
        sb.AppendLine($"URL: {job.VideoMetadata.Url}");
        sb.AppendLine($"形式/品質: {job.Format} / {job.Quality}");
        sb.AppendLine($"保存先: {job.SaveFolderPath}");
    }

    private static void AppendAttemptDetail(StringBuilder sb, string label, IReadOnlyList<string> arguments, YtDlpRunResult run)
    {
        sb.AppendLine($"--- {label} ---");
        sb.AppendLine($"失敗フェーズ: {run.LastPhase}");
        sb.AppendLine($"終了コード: {run.ExitCode}");
        sb.AppendLine($"経過時間: {run.Elapsed.TotalSeconds:F1}秒");
        sb.AppendLine($"実行コマンド: {FormatArgumentsForLog(arguments)}");
        sb.AppendLine("stdout要約:");
        sb.AppendLine(IndentBlock(run.StdOutSummary));
        sb.AppendLine("stdout診断:");
        sb.AppendLine(IndentBlock(run.StdOutDiagnostics));
        sb.AppendLine("stderr:");
        sb.Append(IndentBlock(run.StdErr));
    }

    /// <summary>複数行テキストを2スペースインデントして読みやすくする（空なら「(出力なし)」）</summary>
    private static string IndentBlock(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "  (出力なし)";
        }

        var sb = new StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").TrimEnd().Split('\n'))
        {
            sb.Append("  ").AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }
}
