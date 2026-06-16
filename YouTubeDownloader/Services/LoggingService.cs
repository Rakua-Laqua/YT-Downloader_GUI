using System;
using System.IO;
using System.Text;

namespace YouTubeDownloader.Services;

/// <summary>
/// アプリケーションのログをテキストファイルへ出力するサービス。
/// 設計書 §5.1 の LoggingService / F-07「必要に応じてエラーログをテキストファイルに出力する」に対応する。
/// </summary>
public interface ILoggingService
{
    /// <summary>ログファイルの出力先ディレクトリ</summary>
    string LogDirectory { get; }

    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}

/// <summary>
/// <see cref="ILoggingService"/> の既定実装。
/// %LocalAppData%\YouTubeDownloader\logs\app-yyyyMMdd.log に日付ローテーションで追記する。
/// 複数のダウンロードワーカーから同時に書かれるため、書き込みはロックで直列化する。
/// </summary>
public class LoggingService : ILoggingService
{
    private readonly object _writeLock = new();

    /// <summary>この日数より古いログファイルは起動後の初回書き込み時に削除する</summary>
    private const int RetentionDays = 14;

    /// <summary>同一日に何度も削除処理を走らせないためのガード</summary>
    private DateTime _lastCleanupDate = DateTime.MinValue;

    public string LogDirectory { get; }

    public LoggingService()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        LogDirectory = Path.Combine(appDataPath, "YouTubeDownloader", "logs");
        try
        {
            Directory.CreateDirectory(LogDirectory);
        }
        catch
        {
            // 生成失敗時は書き込み時に再度試みる
        }
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null)
    {
        if (exception == null)
        {
            Write("ERROR", message);
            return;
        }

        var sb = new StringBuilder();
        sb.Append(message);
        AppendException(sb, exception, "例外");
        Write("ERROR", sb.ToString());
    }

    private static void AppendException(StringBuilder sb, Exception exception, string label)
    {
        sb.AppendLine();
        sb.Append("  ").Append(label).Append(": ")
          .Append(exception.GetType().FullName).Append(": ").Append(exception.Message);

        if (!string.IsNullOrEmpty(exception.StackTrace))
        {
            sb.AppendLine();
            sb.AppendLine("  スタックトレース:");
            sb.Append(IndentLines(exception.StackTrace, "    "));
        }

        if (exception.InnerException != null)
        {
            AppendException(sb, exception.InnerException, "内部例外");
        }
    }

    private void Write(string level, string message)
    {
        try
        {
            var now = DateTime.Now;
            var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
            var path = Path.Combine(LogDirectory, $"app-{now:yyyyMMdd}.log");

            lock (_writeLock)
            {
                Directory.CreateDirectory(LogDirectory);
                CleanupOldLogs(now);
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // ログ書き込みの失敗はアプリ本体の動作に影響させない
        }
    }

    private void CleanupOldLogs(DateTime now)
    {
        if (_lastCleanupDate.Date == now.Date)
        {
            return;
        }
        _lastCleanupDate = now;

        try
        {
            var threshold = now.Date.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(LogDirectory, "app-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < threshold)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // 個別の削除失敗は無視
                }
            }
        }
        catch
        {
            // 一覧取得失敗時も無視
        }
    }

    private static string IndentLines(string text, string indent)
    {
        var sb = new StringBuilder();
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            sb.Append(indent).AppendLine(rawLine);
        }
        return sb.ToString().TrimEnd();
    }
}
