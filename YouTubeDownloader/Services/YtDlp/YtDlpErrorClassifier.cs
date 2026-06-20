using System;

namespace YouTubeDownloader.Services;

/// <summary>
/// yt-dlpの標準エラー出力を既知のパターンに分類し、
/// 「実際に何が起きているか（原因）」と「どう対処すればよいか（対処）」を日本語で返す。
/// yt-dlpが出す生のメッセージは英語だったり、YouTubeがそのまま返す紛らわしい文面
/// （例: "Sign in to confirm you're not a bot" = 実態はIPの一時的なbot判定）だったりするため、
/// 利用者が次の行動を取れる正確な説明に翻訳する目的で使う。
/// 併せて、別のplayer clientでの再試行に意味があるか（RetryWorthwhile）も返す。
/// </summary>
internal static class YtDlpErrorClassifier
{
    /// <summary>
    /// 分類結果。Category=短い分類名、Summary=原因の正確な説明、Advice=推奨される対処、
    /// RetryWorthwhile=別clientでの再試行で回復し得るか（falseなら再試行は無駄＝即失敗にしてよい）。
    /// </summary>
    public sealed record Diagnosis(string Category, string Summary, string Advice, bool RetryWorthwhile);

    private sealed record Rule(string Category, string Summary, string Advice, bool RetryWorthwhile, string[] Keywords);

    // 上から順に評価し、最初にキーワードが一致したルールを採用する。
    // 「Sign in to confirm your age」と「Sign in to confirm you're not a bot」のように
    // 文面が一部重なるため、より限定的なもの（年齢制限・メンバー限定・非公開）を先に置く。
    //
    // RetryWorthwhile=false は「外部条件（cookie・地域・正しいURL・IPの冷却）を変えない限り
    // 取得できない」もの。別clientへ切り替えても直らず、特に bot/429 は無駄な再アクセスが
    // IP制限を悪化させるため、再試行せず即失敗にする。
    private static readonly Rule[] Rules =
    {
        new("年齢制限",
            "年齢制限付き動画です。取得には年齢確認済みアカウントでのログインが必要です。",
            "年齢確認済みGoogleアカウントのcookie（ログイン情報）が必要です。cookieなしでは取得できません。",
            false,
            new[] { "confirm your age", "age-restricted", "age restricted", "inappropriate for some", "年齢確認", "年齢を確認", "年齢制限" }),

        new("メンバー限定",
            "チャンネルメンバー限定の動画です。",
            "メンバーシップに加入済みのアカウントのcookie（ログイン情報）が必要です。",
            false,
            new[] { "members-only", "join this channel", "channel members", "メンバー限定", "メンバーシップ" }),

        new("非公開動画",
            "非公開（プライベート）に設定された動画です。",
            "視聴権限のあるアカウントのcookieが必要です。一般には取得できません。",
            false,
            new[] { "private video", "video is private", "非公開" }),

        new("認証要求（bot検知）",
            "YouTubeのbot検知に阻まれました。動画固有の問題ではなく、アクセス元IPが一時的に自動アクセス（bot）とみなされている状態です。短時間に多数のリクエストを送ると発生しやすくなります。",
            "しばらく時間を空けてから再試行してください。連続・並列ダウンロードを控えると改善します。根本的に回避するにはcookie（ログイン情報）による認証が必要です。",
            false,
            new[] { "not a bot", "bot ではない", "ログインして bot", "sign in to confirm you" }),

        new("レート制限（429）",
            "YouTubeにアクセス過多（HTTP 429 Too Many Requests）と判定され、IPが一時的に制限されています。",
            "しばらく時間を空けてから再試行してください。同時ダウンロード数や並列フラグメント数を下げると発生しにくくなります。",
            false,
            new[] { "http error 429", "429: too many requests", "too many requests" }),

        new("著作権ブロック",
            "著作権を理由にブロックされている動画です。",
            "この動画は取得できません。",
            false,
            new[] { "copyright grounds", "on copyright", "著作権" }),

        new("地域制限",
            "地域制限により、現在の地域では視聴できない動画です。",
            "配信対象の地域からのアクセス（VPN等）が必要です。",
            false,
            new[] { "available in your", "blocked it in your country", "geo restrict", "geo-restrict", "地域制限", "地域で視聴" }),

        new("動画が利用不可",
            "動画が削除済み、または現在利用できない状態です。",
            "URLが正しいか、動画が公開中かを確認してください。",
            true,
            new[] { "video unavailable", "this video is not available", "no longer available", "has been removed", "removed by the uploader", "has been terminated", "削除", "この動画は利用できません" }),

        new("公開前（ライブ/プレミア）",
            "まだ開始していないライブ配信、またはプレミア公開です。",
            "公開が始まってから再試行してください。",
            true,
            new[] { "live event will begin", "this live event", "premieres in", "premiere will begin", "ライブ配信", "プレミア公開" }),

        new("形式が利用不可",
            "指定した形式・画質が、この動画では提供されていません。",
            "形式や画質の設定を変更して再試行してください。",
            true,
            new[] { "requested format is not available", "requested format", "format is not available" }),

        new("ffmpeg不足",
            "結合・変換に必要なffmpeg/ffprobeが見つかりませんでした。",
            "設定でffmpegの場所（パス）が正しいか確認してください。",
            true,
            new[] { "ffmpeg is not installed", "ffmpeg not found", "ffprobe and ffmpeg", "unable to locate ffmpeg" }),

        new("ネットワークエラー",
            "ネットワークに接続できませんでした。",
            "インターネット接続・プロキシ・ファイアウォールの設定を確認して再試行してください。",
            true,
            new[] { "getaddrinfo", "failed to resolve", "name or service not known", "temporary failure in name resolution", "connection refused", "connection reset", "connection aborted", "network is unreachable", "remote end closed", "timed out", "max retries exceeded", "[winerror 10054]", "[winerror 10060]", "[errno 11001]" }),

        new("URLエラー",
            "URLを認識できませんでした（対応していないURLの可能性）。",
            "対応サイトの正しい動画URLか確認してください。",
            false,
            new[] { "unsupported url", "is not a valid url" }),
    };

    /// <summary>
    /// stderr（必要に応じて他の出力を連結したもの）を分類する。
    /// 既知のパターンに一致しなければ null を返す（その場合は呼び出し側で生のエラー文をそのまま見せる）。
    /// </summary>
    public static Diagnosis? Classify(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return null;
        }

        foreach (var rule in Rules)
        {
            foreach (var keyword in rule.Keywords)
            {
                if (stderr.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return new Diagnosis(rule.Category, rule.Summary, rule.Advice, rule.RetryWorthwhile);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 別のplayer clientでの再試行に意味があるか。
    /// 既知の回復不能エラー（bot検知・年齢制限・非公開・429 等）なら false。
    /// 未知のエラーは判断材料が無いため、一応 true（再試行する）とする。
    /// </summary>
    public static bool IsRetryWorthwhile(string? stderr)
    {
        return Classify(stderr)?.RetryWorthwhile ?? true;
    }
}
