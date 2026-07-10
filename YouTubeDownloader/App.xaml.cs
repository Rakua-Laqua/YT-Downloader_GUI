using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using YouTubeDownloader.Services;
using YouTubeDownloader.ViewModels;

namespace YouTubeDownloader;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private readonly IServiceProvider _serviceProvider;

    public App()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // 基盤
        services.AddSingleton<ILoggingService, LoggingService>();

        // リポジトリ
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<IMetadataRepository, MetadataRepository>();

        // サービス
        services.AddSingleton<IYtDlpClient, YtDlpClient>();
        services.AddSingleton<IDownloadManager, DownloadManager>();

        // ViewModels
        services.AddTransient<DownloadViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddSingleton<MainWindowViewModel>(sp =>
        {
            return new MainWindowViewModel(
                () => sp.GetRequiredService<DownloadViewModel>(),
                () => sp.GetRequiredService<LibraryViewModel>(),
                () => sp.GetRequiredService<SettingsViewModel>()
            );
        });

        // Views
        services.AddSingleton<MainWindow>();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // DI コンテナを破棄して DownloadManager.Dispose を呼び、実行中の
            // yt-dlp プロセスへ渡したキャンセルトークンを停止させる。
            (_serviceProvider as IDisposable)?.Dispose();
        }
        finally
        {
            base.OnExit(e);
        }
    }
}

