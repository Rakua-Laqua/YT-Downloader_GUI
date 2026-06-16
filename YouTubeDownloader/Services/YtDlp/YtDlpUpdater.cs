using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace YouTubeDownloader.Services;

internal sealed class YtDlpUpdater
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly ILoggingService _logger;
    private readonly Func<string> _getYtDlpPath;
    private readonly SemaphoreSlim _ytDlpUpdateLock = new(1, 1);
    private bool _hasCheckedYtDlpUpdate;

    public YtDlpUpdater(ISettingsRepository settingsRepository, ILoggingService logger, Func<string> getYtDlpPath)
    {
        _settingsRepository = settingsRepository;
        _logger = logger;
        _getYtDlpPath = getYtDlpPath;
    }

    public async Task<YtDlpUpdateResult> UpdateYtDlpAsync(string? channelOverride = null, CancellationToken cancellationToken = default)
    {
        var ytDlpPath = _getYtDlpPath();

        // 設定保存前でもUIで選択中のチャンネルで更新できるよう、明示指定があればそれを優先する。
        var channel = NormalizeUpdateChannel(
            !string.IsNullOrWhiteSpace(channelOverride) ? channelOverride : _settingsRepository.Load().YtDlpUpdateChannel);

        await _ytDlpUpdateLock.WaitAsync(cancellationToken);
        try
        {
            var result = await RunYtDlpUpdateAsync(ytDlpPath, channel, cancellationToken);
            _hasCheckedYtDlpUpdate = true;
            return result;
        }
        finally
        {
            _ytDlpUpdateLock.Release();
        }
    }

    public async Task<string?> GetYtDlpVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var ytDlpPath = _getYtDlpPath();
            var output = await YtDlpProcessRunner.RunAsync(ytDlpPath, new[] { "--version" }, cancellationToken);
            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        catch
        {
            return null;
        }
    }

    public async Task EnsureYtDlpUpdatedAsync(string ytDlpPath, CancellationToken cancellationToken)
    {
        var settings = _settingsRepository.Load();
        if (!settings.AutoUpdateYtDlp || _hasCheckedYtDlpUpdate)
        {
            return;
        }

        await _ytDlpUpdateLock.WaitAsync(cancellationToken);
        try
        {
            if (_hasCheckedYtDlpUpdate)
            {
                return;
            }

            var channel = NormalizeUpdateChannel(settings.YtDlpUpdateChannel);
            var result = await RunYtDlpUpdateAsync(ytDlpPath, channel, cancellationToken);
            _hasCheckedYtDlpUpdate = true;

            if (!result.IsSuccess)
            {
                // 自動更新失敗は致命的ではないが、後続のダウンロード失敗の伏線になりやすいので記録する
                _logger.Warn($"yt-dlp 自動更新に失敗しました (channel={channel}): {result.Output}");
            }
        }
        finally
        {
            _ytDlpUpdateLock.Release();
        }
    }

    /// <summary>更新チャンネル文字列を "stable" / "nightly" のいずれかに正規化する</summary>
    private static string NormalizeUpdateChannel(string? channel)
    {
        return string.Equals(channel?.Trim(), "nightly", StringComparison.OrdinalIgnoreCase)
            ? "nightly"
            : "stable";
    }

    private static async Task<YtDlpUpdateResult> RunYtDlpUpdateAsync(string ytDlpPath, string channel, CancellationToken cancellationToken)
    {
        // --update-to <channel> は stable/nightly 間の切り替え（ダウングレード含む）に対応する。
        // 単なる -U は現在のチャンネル内でしか更新しないため、nightlyへ切り替えるにはこちらを使う。
        var (stdout, stderr, exitCode) = await YtDlpProcessRunner.RunRawAsync(
            ytDlpPath,
            new[] { "--update-to", channel },
            cancellationToken);

        var output = string.Join(
            Environment.NewLine,
            new[] { stdout, stderr }.Where(text => !string.IsNullOrWhiteSpace(text)));

        return new YtDlpUpdateResult
        {
            IsSuccess = exitCode == 0,
            Message = exitCode == 0 ? BuildUpdateSuccessMessage(output) : "yt-dlpの更新に失敗しました。",
            Output = output
        };
    }

    private static string BuildUpdateSuccessMessage(string output)
    {
        // 注意: --update-to の出力には常に "Latest version:" 行が含まれるため、
        // 「更新した」判定(Updated/Updating)を先に行う必要がある。
        if (output.Contains("Updated yt-dlp", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Updating to", StringComparison.OrdinalIgnoreCase))
        {
            return "yt-dlpを更新しました。";
        }

        if (output.Contains("is up to date", StringComparison.OrdinalIgnoreCase))
        {
            return "yt-dlpは最新です。";
        }

        return "yt-dlpの更新チェックが完了しました。";
    }
}
