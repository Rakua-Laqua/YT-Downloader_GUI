using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouTubeDownloader.Infrastructure;
using YouTubeDownloader.Services;

namespace YouTubeDownloader.ViewModels;

/// <summary>
/// ライブラリ画面のViewModel
/// </summary>
public partial class LibraryViewModel : ViewModelBase
{
    private readonly IMetadataRepository _metadataRepository;
    private static readonly HashSet<string> SupportedMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mkv",
        ".webm",
        ".mp3",
        ".m4a",
        ".wav"
    };

    // 一括選択時に項目ごとの通知が大量発生するのを抑え、最後に1回だけ件数を更新するためのフラグ
    private bool _suppressSelectionNotifications;

    // ライブ検索のデバウンス（入力が落ち着いてから検索を実行する）
    private const int SearchDebounceMilliseconds = 250;
    private CancellationTokenSource? _searchDebounceCts;

    // 読み込みの再入を防ぐ（ライブ検索とEnter/更新が重なっても一覧の再構築を直列化する）
    private bool _isLoading;
    private bool _pendingReload;

    public LibraryViewModel(IMetadataRepository metadataRepository)
    {
        _metadataRepository = metadataRepository;
        Items.CollectionChanged += OnItemsCollectionChanged;
    }

    #region プロパティ

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    // 検索ボックスへの入力ごとにライブ検索を実行する（デバウンス付き）
    partial void OnSearchQueryChanged(string value) => _ = DebounceSearchAsync();

    [ObservableProperty]
    private VideoMetadataViewModel? _selectedItem;

    public ObservableCollection<VideoMetadataViewModel> Items { get; } = new();

    /// <summary>ライブラリ全体の件数</summary>
    public int TotalCount => Items.Count;

    /// <summary>チェックされている件数</summary>
    public int SelectedCount => Items.Count(i => i.IsSelected);

    /// <summary>1件以上選択されているか（一括削除ボタンの有効/無効に使用）</summary>
    public bool HasSelection => SelectedCount > 0;

    /// <summary>読み込み中でなく、かつ0件か（空状態表示用）</summary>
    public bool IsEmpty => !IsBusy && Items.Count == 0;

    /// <summary>「全 N 件 ・ 選択 M 件」のサマリー表示</summary>
    public string CountSummaryText => SelectedCount > 0
        ? $"全 {TotalCount} 件 ・ 選択 {SelectedCount} 件"
        : $"全 {TotalCount} 件";

    /// <summary>ヘッダーの全選択チェックボックス用（一部選択時は null=不確定）</summary>
    public bool? AreAllSelected
    {
        get
        {
            if (Items.Count == 0)
            {
                return false;
            }

            var selected = SelectedCount;
            if (selected == 0) return false;
            if (selected == Items.Count) return true;
            return null;
        }
        set
        {
            if (value == true)
            {
                SetAllSelection(true);
            }
            else
            {
                SetAllSelection(false);
            }
        }
    }

    #endregion

    #region コマンド

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadItemsAsync(showBusy: true);
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        await LoadItemsAsync(showBusy: true);
    }

    /// <summary>
    /// 現在の検索クエリでライブラリを再読み込みする。
    /// ライブ検索（入力ごと）の呼び出しでは、毎キーストロークでビジー表示が点滅しないよう showBusy=false で呼ぶ。
    /// </summary>
    private async Task LoadItemsAsync(bool showBusy)
    {
        // 既に読み込み中なら、最新の結果になるよう完了後に1回だけ再実行する
        if (_isLoading)
        {
            _pendingReload = true;
            return;
        }

        _isLoading = true;

        if (showBusy)
        {
            IsBusy = true;
            BusyMessage = "読み込み中...";
            OnPropertyChanged(nameof(IsEmpty));
        }

        try
        {
            ClearItems();
            var videos = await _metadataRepository.FindVideosAsync(SearchQuery);
            foreach (var video in videos)
            {
                Items.Add(new VideoMetadataViewModel(video));
            }
        }
        finally
        {
            if (showBusy)
            {
                IsBusy = false;
                BusyMessage = null;
            }
            OnPropertyChanged(nameof(IsEmpty));
            _isLoading = false;
        }

        if (_pendingReload)
        {
            _pendingReload = false;
            await LoadItemsAsync(showBusy: false);
        }
    }

    [RelayCommand]
    private void SelectAll() => SetAllSelection(true);

    [RelayCommand]
    private void ClearSelection() => SetAllSelection(false);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteSelectedAsync()
    {
        var targets = Items.Where(i => i.IsSelected).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var result = MessageBox.Show(
            $"選択した {targets.Count} 件を履歴から削除しますか？\n\nファイル自体は削除されません。",
            "確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        await _metadataRepository.DeleteVideoMetadataAsync(targets.Select(t => t.Metadata.Id));
        foreach (var target in targets)
        {
            Items.Remove(target);
        }
    }

    [RelayCommand]
    private void OpenFolder(VideoMetadataViewModel? item)
    {
        if (!TryResolveExistingLocalFile(item?.LocalFilePath, requireSupportedMedia: false, out var localFilePath))
        {
            return;
        }

        var folder = Path.GetDirectoryName(localFilePath);
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add($"/select,{localFilePath}");
            try
            {
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"フォルダを開けませんでした: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private void PlayFile(VideoMetadataViewModel? item)
    {
        if (!TryResolveExistingLocalFile(item?.LocalFilePath, requireSupportedMedia: true, out var localFilePath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = localFilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ファイルを開けませんでした: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task DeleteItemAsync(VideoMetadataViewModel? item)
    {
        if (item == null) return;

        var result = MessageBox.Show(
            $"「{item.Title}」を履歴から削除しますか？\n\nファイル自体は削除されません。",
            "確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            await _metadataRepository.DeleteVideoMetadataAsync(item.Metadata.Id);
            Items.Remove(item);
        }
    }

    [RelayCommand]
    private void OpenVideoLink(VideoMetadataViewModel? item)
    {
        if (item == null)
        {
            return;
        }

        // 確認ダイアログ＋ブラウザ起動はダウンロード画面と共通
        ExternalLinkOpener.ConfirmAndOpenVideoLink(item.Url, item.Title);
    }

    #endregion

    #region ライブ検索

    private async Task DebounceSearchAsync()
    {
        // 直前の待機をキャンセルし、最新の入力だけを実行対象にする
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;

        try
        {
            await Task.Delay(SearchDebounceMilliseconds, cts.Token);
            // ライブ検索ではビジー表示を出さず、ちらつきを防ぐ
            await LoadItemsAsync(showBusy: false);
        }
        catch (OperationCanceledException)
        {
            // 入力が続いている間はキャンセルされる（正常）
        }
        catch (Exception ex)
        {
            // プロパティ変更から起動されるため、例外をUI同期コンテキストへ逸脱させない。
            Trace.TraceError($"ライブ検索に失敗しました: {ex}");
        }
    }

    #endregion

    #region 選択・件数管理

    private void SetAllSelection(bool selected)
    {
        _suppressSelectionNotifications = true;
        foreach (var item in Items)
        {
            item.IsSelected = selected;
        }
        _suppressSelectionNotifications = false;

        RaiseCountsChanged();
    }

    private void ClearItems()
    {
        foreach (var item in Items)
        {
            item.SelectionChanged -= OnItemSelectionChanged;
        }
        Items.Clear();
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (VideoMetadataViewModel item in e.OldItems)
            {
                item.SelectionChanged -= OnItemSelectionChanged;
            }
        }

        if (e.NewItems != null)
        {
            foreach (VideoMetadataViewModel item in e.NewItems)
            {
                item.SelectionChanged += OnItemSelectionChanged;
            }
        }

        RaiseCountsChanged();
    }

    private void OnItemSelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressSelectionNotifications)
        {
            return;
        }

        RaiseCountsChanged();
    }

    private void RaiseCountsChanged()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountSummaryText));
        OnPropertyChanged(nameof(AreAllSelected));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    #endregion

    public async Task LoadAsync()
    {
        await RefreshAsync();
    }

    private static bool TryResolveExistingLocalFile(string? path, bool requireSupportedMedia, out string localFilePath)
    {
        localFilePath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            localFilePath = Path.GetFullPath(path);
        }
        catch
        {
            MessageBox.Show("ファイルパスが不正です。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!File.Exists(localFilePath))
        {
            MessageBox.Show("ファイルが見つかりません。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (requireSupportedMedia && !SupportedMediaExtensions.Contains(Path.GetExtension(localFilePath)))
        {
            MessageBox.Show("対応していないファイル形式のため、アプリからは開けません。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }
}
