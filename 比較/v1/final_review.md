
前回（評価内容.md）でカバーされている指摘、および直近の差分で**指摘1・2・3が既に修正済み**であることを確認しました。その上で、**前回挙がっていない新しい観点**だけを以下にまとめます。

なお重大な機能不全（キャンセルでプロセスが残る等）は既に対処済みのため、今回は機能上の実害が生じ得るバグ・設計上の論理穴・ロケール依存・保守性・拡張性が中心です。

---

## 【中-高】1. ファイル名テンプレートが空・固定名になるケースで、`--force-overwrites` と組み合わさりデータ損失が起こり得る

**1. 改善すべき点**
[YtDlpClient.cs:761-787](YouTubeDownloader/Services/YtDlpClient.cs#L761) の `BuildFilenameTemplate` は、テンプレートの各プレースホルダを置換した後に `result.Trim()` し、拡張子がなければ `.%(ext)s` を付与するだけで、**結果が空になった場合のフォールバックがありません**。たとえばテンプレートが `{index}` のみで単一動画（`PlaylistIndex = null`）の場合、`{index}` → `""` → Trim後も `""` → 最終結果は `.%(ext)s` となり、ファイル名が `.mp4` のようになります。さらに [YtDlpClient.cs:408](YouTubeDownloader/Services/YtDlpClient.cs#L408) で `--force-overwrites` が常に付与されているため、**プレイリスト内の複数動画が同一ファイル名で次々に上書きされ、最後の1件以外は消失**します。

**2. 優先度**：中-高

**3. 理由**
発生条件（空テンプレートや一意性のないテンプレート）は通常の利用では稀ですが、**発生した場合のインパクトはダウンロード済みファイルの損失**であり、取り返しがつきません。設定画面でテンプレートを自由入力できる以上、防御が必要です。

**4. 具体的な対応案**
`BuildFilenameTemplate` の末尾で、ベース名が空または一意性が低い場合にフォールバックを入れます。
```csharp
result = result.Trim();

// フォールバック: ベース名が空なら安全なデフォルトを使う
if (string.IsNullOrEmpty(result))
{
    result = SanitizeFilename(job.VideoMetadata.Title);
    if (string.IsNullOrEmpty(result))
        result = job.VideoMetadata.Id;
}

if (!Path.HasExtension(result))
    result += $".%(ext)s";
```
加えて、設定保存時に `SettingsViewModel` 側でテンプレートを検証し、展開後にタイトルや ID が含まれない（一意性が保証できない）場合は警告を出すと堅いです。

**5. 注意点**
既存ユーザーが意図的に固定名保存している可能性もゼロではないため、強制変更ではなく「警告＋フォールバック」の段階的導入が無難です。

---

## 【中】2. 完了後の `LocalFilePath` 特定が `Directory.GetFiles(...)[0]` で曖昧

**1. 改善すべき点**
[YtDlpClient.cs:560-570](YouTubeDownloader/Services/YtDlpClient.cs#L560) の `UpdateLocalFilePath` は、テンプレートのベース名でワイルドカード検索し `files[0]` を取得します。しかしこの方法では、**古い同名ファイル、サムネイル（`.jpg`）、別形式（`.webm`）、部分ダウンロード（`.part`）** なども一致し得ます。特に指摘1のテンプレート問題で複数動画が同名になるケースでは、**今回ダウンロードした動画とは異なるファイルがライブラリに登録**される可能性があります。結果は [DownloadManager.cs:115](YouTubeDownloader/Services/DownloadManager.cs#L115) でそのまま履歴保存されます。

**2. 優先度**：中

**3. 理由**
ライブラリの再生やフォルダ表示が実際のダウンロードファイルとズレるため、ユーザー体験に直接影響します。

**4. 具体的な対応案**
最も確実なのは yt-dlp の `--print after_move:filepath` オプションで最終出力パスを受け取る方法です。
```csharp
// ダウンロード引数に追加
args.Append("--print after_move:filepath ");

// stdout の最終行から実パスを取得
```
簡易対応なら、ダウンロード開始時刻を記録し、期待拡張子で絞った上で `LastWriteTimeUtc` が開始時刻以降のファイルを優先選択します。

**5. 注意点**
音声抽出やマージ後の拡張子は yt-dlp 側で決まるため、拡張子推定だけに寄せすぎない方が安全です。`--print after_move:filepath` は yt-dlp 2022.01.21 以降で対応しています。

---

## 【中】3. 解析処理が多重実行でき、古い結果が UI を上書きする可能性がある

**1. 改善すべき点**
[DownloadViewModel.cs:126-149](YouTubeDownloader/ViewModels/DownloadViewModel.cs#L126) の `AnalyzeAsync` は `IsAnalyzing` フラグを立てますが、**再入防止（`CanExecute` 連動）がありません**。`RelayCommand` は async メソッドの完了を待たずに再実行可能なため、重いプレイリスト解析中に別 URL の解析ボタンを押すと、先に開始した処理が後から完了して `_currentVideo` / `_currentPlaylist` を古い内容に戻すリスクがあります。

**2. 優先度**：中

**3. 理由**
プレイリスト解析は数秒〜十数秒かかることがあり、ユーザーが「別の URL を試そう」と操作する場面は十分にあり得ます。結果の上書きは「直前に入力した URL と違う動画がダウンロードされる」という深刻な混乱を招きます。

**4. 具体的な対応案**
最小限の対応は再入防止ガードです。
```csharp
[RelayCommand(CanExecute = nameof(CanAnalyze))]
private async Task AnalyzeAsync()
{
    // ...
}

private bool CanAnalyze() => !IsAnalyzing && !string.IsNullOrWhiteSpace(InputUrl);

// IsAnalyzing / InputUrl の変更時に通知
partial void OnIsAnalyzingChanged(bool value) => AnalyzeCommand.NotifyCanExecuteChanged();
partial void OnInputUrlChanged(string value) => AnalyzeCommand.NotifyCanExecuteChanged();
```
より堅い対応として、`var requestedUrl = InputUrl.Trim();` を保持し、await 後に現在の入力と一致しない結果は破棄する方法もあります。

**5. 注意点**
解析キャンセルまで入れると変更範囲が広がるため、まずは多重実行防止と古い結果の破棄だけで十分です。

---

## 【中】4. 進捗パースの `double.TryParse` がカルチャ非依存でなく、一部ロケールで進捗が壊れる

**1. 改善すべき点**
[YtDlpClient.cs:812](YouTubeDownloader/Services/YtDlpClient.cs#L812) の `if (double.TryParse(percentStr, out var percent))` はカルチャ未指定です。この overload は `CultureInfo.CurrentCulture` を使うため、**小数点にカンマを用いるロケール（ドイツ語・フランス語など）** では yt-dlp が出力する `"50.0"` の `.` が桁区切りと解釈され、`50.0 → 500` になったりパース失敗（0%）になります。同ファイルの `upload_date` 解析（[L216](YouTubeDownloader/Services/YtDlpClient.cs#L216)）は `CultureInfo.InvariantCulture` を使っており、ここだけ不整合です。

**2. 優先度**：中

**3. 理由**
yt-dlp の数値出力は常にドット小数なので、外部プロセス出力のパースは必ず InvariantCulture にすべきです。放置すると非日本語ロケール環境で「進捗バーが 0% のまま」「ステータスが `(500%)` 表示」といった再現条件の分かりにくい不具合になります。

**4. 具体的な対応案**
```csharp
if (double.TryParse(percentStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
```
`ParseProgressLine` 内の数値パースを InvariantCulture 固定にします。

**5. 注意点**
日本語環境では現状でも `.` が小数点なので挙動は変わりません（既存動作維持）。影響は局所的で互換性問題なし。

---

## 【中】5. 自動更新／解析プロセスがキャンセル時に kill されない（download path との非対称）

**1. 改善すべき点**
前回指摘1の修正で `RunDownloadProcessAsync` には `cancellationToken.Register(() => process.Kill(...))`（[YtDlpClient.cs:505](YouTubeDownloader/Services/YtDlpClient.cs#L505)）が入りましたが、**`RunYtDlpUpdateAsync`（[L709](YouTubeDownloader/Services/YtDlpClient.cs#L709)）と `RunYtDlpAsync`（[L846](YouTubeDownloader/Services/YtDlpClient.cs#L846)）には kill 登録がありません**。特に `EnsureYtDlpUpdatedAsync` は `DownloadAsync` 内で**ジョブのトークン付き**で呼ばれる（[L349](YouTubeDownloader/Services/YtDlpClient.cs#L349)）ため、「初回ダウンロードの更新フェーズ中にキャンセル」を押すと、`ReadToEndAsync(token)`/`WaitForExitAsync(token)` が OCE を投げてジョブは Canceled になるものの、`yt-dlp -U` プロセス自体は走り続けます。

**2. 優先度**：中

**3. 理由**
前回指摘1で「ダウンロードは止まるが更新・解析は孤児化し得る」という抜けが残っています。`yt-dlp -U` は自己バイナリを置き換える処理なので、中断・孤児化はバイナリ破損や次回起動時のロックにつながる可能性があります。

**4. 具体的な対応案**
方針は2択で、**こちらは方針確認が必要**です。
- **更新は中断させない**のが安全（推奨）：`EnsureYtDlpUpdatedAsync` 内の更新呼び出しを `CancellationToken.None` で実行し、ダウンロードのキャンセルが自己更新を巻き込まないようにする。
- もしくは `RunDownloadProcessAsync` と同じ kill 登録を共通化して update/analyze にも適用する（ただし自己更新の途中 kill はバイナリ破損リスクがあるため、更新には不向き）。

**5. 注意点**
`-U` の途中 kill は逆効果になり得るため、安易に kill 登録を横展開せず「更新だけはキャンセル非対応にする」方が無難です。解析（`RunYtDlpAsync`）側は UI から token を渡していない（[DownloadViewModel.cs:143](YouTubeDownloader/ViewModels/DownloadViewModel.cs#L143)）ので現状キャンセル経路はなく、実害は更新パスが中心です。

---

## 【中】6. 解析用の `RunYtDlpAsync` が exit code と stderr を捨てており、失敗理由が UI に出ない

**1. 改善すべき点**
[YtDlpClient.cs:846-872](YouTubeDownloader/Services/YtDlpClient.cs#L846) の `RunYtDlpAsync` は stdout のみを返却し、exit code と stderr を捨てています。非公開動画、地域制限、削除済み動画、yt-dlp 側のバグなどで解析が失敗した場合、すべてが「情報を取得できませんでした」に丸められ、**ユーザーも保守側も原因を追えません**。

**2. 優先度**：中

**3. 理由**
stderr には yt-dlp が出力する具体的なエラーメッセージ（`"Video unavailable"`, `"Sign in to confirm your age"` 等）が含まれており、これをユーザーに伝えるだけでも自己解決率が大幅に向上します。

**4. 具体的な対応案**
`RunYtDlpAsync` の戻り値を拡張します。
```csharp
private record YtDlpResult(int ExitCode, string StdOut, string StdErr);

private static async Task<YtDlpResult> RunYtDlpAsync(
    string ytDlpPath, string arguments, CancellationToken cancellationToken)
{
    // ... 既存のプロセス起動コード ...
    return new YtDlpResult(process.ExitCode, outputTask.Result, errorTask.Result);
}
```
呼び出し側で `ExitCode != 0` の場合に stderr の末尾数行を `ErrorMessage` に含めます。

**5. 注意点**
stderr には警告も出るので、stderr の有無ではなく exit code を主判定にするのが安全です。既存の stderr 読み捨てコメント（L863-864）が示す通り、stderr のリダイレクト自体は既に行われているため、読むだけなら変更は小さいです。

---

## 【中】7. `DownloadViewModel`（Transient）が Singleton の `DownloadManager` イベントを購読し、解除しない

**1. 改善すべき点**
[DownloadViewModel.cs:37-38](YouTubeDownloader/ViewModels/DownloadViewModel.cs#L37) で `JobProgressChanged`/`JobStatusChanged` を購読していますが、`DownloadViewModel` は DI 上 `AddTransient`（[App.xaml.cs:34](YouTubeDownloader/App.xaml.cs#L34)）で、購読解除（`IDisposable`）もありません。現状は `MainWindowViewModel` が `_downloadViewModel ??= factory()`（[MainWindowViewModel.cs:53](YouTubeDownloader/ViewModels/MainWindowViewModel.cs#L53)）でキャッシュするため**実際には1個しか生成されず実害は出ていません**。ただ「Transient + イベント購読 + 非Dispose」の組み合わせは、将来ビューを作り直す設計に変えた瞬間にリーク＆二重発火（同じ進捗更新が複数 VM に飛ぶ）を招く地雷です。

**2. 優先度**：中（現状は潜在的、設計上の注意）

**3. 理由**
実態が Singleton なら DI 登録もそれに合わせる方が意図が明確で、誤った再利用を防げます。逆に Transient のままにするなら購読解除が必須です。

**4. 具体的な対応案**
いずれか。
- `DownloadViewModel`/`LibraryViewModel`/`SettingsViewModel` を `AddSingleton` に変更（`MainWindowViewModel` のキャッシュと整合）。
- もしくは `DownloadViewModel : IDisposable` にして `-=` で購読解除し、ビュー破棄時に Dispose する。

**5. 注意点**
Singleton 化すると各ビューの状態が常駐します（入力欄やキュー表示が保持され、むしろ望ましいことが多い）。`LibraryViewModel` だけは「開くたびに最新化」したい場合があるので、`NavigateToLibraryAsync` の `LoadAsync()` 呼び出しは現状維持で問題ありません。

---

## 【中】8. ライブラリがダウンロード完了を自動反映しない

**1. 改善すべき点**
`LibraryViewModel` は `DownloadManager` のイベントを購読していません。ダウンロード完了で `MetadataRepository` に履歴が追加されますが、**既にライブラリ画面を開いている状態では一覧が更新されません**（再ナビゲーション or 更新ボタンが必要）。

**2. 優先度**：中（機能の自然さに関わる）

**3. 理由**
「ダウンロード→ライブラリで確認」という基本動線で、ユーザーが「保存されていない？」と誤解しやすい挙動です。

**4. 具体的な対応案**
`LibraryViewModel` で `JobStatusChanged` を購読し、`Completed` 到達時に該当アイテムを `Items` に追加（または `RefreshAsync` を間引き実行）。Dispatcher マーシャルは `DownloadViewModel` 同様に。指摘7で Singleton 化する場合は購読のライフサイクルも揃えてください。

**5. 注意点**
購読する場合は指摘7の購読解除問題が同じく付きまといます。常時 `RefreshAsync` は全件再読込なので、完了1件ごとの差分追加にする方が効率的です。

---

## 【低】9. `MaybeFlashWhenAllCompleted` が Canceled/Failed 混在時に永遠に発火しない

**1. 改善すべき点**
[DownloadViewModel.cs:408](YouTubeDownloader/ViewModels/DownloadViewModel.cs#L408) は `DownloadQueue.All(j => j.Status == Completed)` のため、キューに**1件でも Canceled/Failed があると、残り全部が完了してもタスクバー点滅通知が出ません**。

**2. 優先度**：低

**3. 理由**
「数十件中1件だけ失敗、残りは完了」でも完了通知が来ないのは、通知機能としては取りこぼしです。仕様として「1件でも未完了扱いなら通知しない」が意図なら現状で正しいです（**意図確認が必要**）。

**4. 具体的な対応案**
「実行中/待機中が0件になったら通知」に変えるのが自然です。
```csharp
var allFinished = DownloadQueue.All(j =>
    j.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Canceled);
var anyCompleted = DownloadQueue.Any(j => j.Status == DownloadStatus.Completed);
if (!allFinished || !anyCompleted) { _hasFlashedForAllCompleted = false; return; }
```

**5. 注意点**
通知の意味づけ（「全成功」なのか「全終了」なのか）が変わるため、UX 方針の確認が必要です。

---

## 【低】10. 設定画面のファイル名プレビューが拡張子 `.mp4` 固定

**1. 改善すべき点**
[SettingsViewModel.cs:287](YouTubeDownloader/ViewModels/SettingsViewModel.cs#L287) の `FilenamePreview = preview + ".mp4";` は常に `.mp4`。`DefaultAudioFormat = "mp3"` や `DefaultVideoFormat = "mkv"` を選んでもプレビューは `.mp4` のままで、実際の保存名と食い違います。

**2. 優先度**：低

**3. 理由**
設定プレビューは「実挙動の確認用」なので、実際の拡張子と一致していないと誤解を招きます。

**4. 具体的な対応案**
`DefaultVideoFormat`（音声モード考慮が必要なら別途）を末尾に使う：`FilenamePreview = preview + "." + DefaultVideoFormat;`。`OnDefaultVideoFormatChanged` でも `UpdateFilenamePreview()` を呼ぶ。

**5. 注意点**
プレビュー専用の表示変更で、保存ロジックには影響なし。

---

## 【低】11. XAML の `BooleanToVisibilityConverter` が用途外に使用されている

**1. 改善すべき点**
[DownloadView.xaml:12](YouTubeDownloader/Views/DownloadView.xaml#L12) で `<BooleanToVisibilityConverter x:Key="BoolToVis"/>` と**WPF 標準コンバーター**が定義されています。しかし以下の箇所で本来の用途と異なる使い方をしています：
- [DownloadView.xaml:73](YouTubeDownloader/Views/DownloadView.xaml#L73)：`Background` プロパティ（`Brush` 型）に `Visibility` 値をバインド → 型不一致でバインディングエラー
- [DownloadView.xaml:322](YouTubeDownloader/Views/DownloadView.xaml#L322)：`DownloadQueue.Count`（`int` 型）を入力し `ConverterParameter=Inverted` → 標準コンバーターは `int` を受け付けず、`Inverted` パラメータも解釈しない
- [LibraryView.xaml:177](YouTubeDownloader/Views/LibraryView.xaml#L177)：同様に `Items.Count` + `Inverted`

**2. 優先度**：低

**3. 理由**
標準 `BooleanToVisibilityConverter` は `bool` → `Visibility` の変換のみ対応しており、`int` 入力や `ConverterParameter=Inverted` は無視されます。結果として「空状態メッセージが表示されない」「背景色が適用されない」といった**見た目の不具合**が発生し得ます。出力ウィンドウにバインディングエラーが出ている可能性が高いです。

**4. 具体的な対応案**
- 背景色の切替は `DataTrigger` で実現する（L73）
- 空表示には専用コンバーターを作るか、ViewModel に `IsQueueEmpty` / `IsLibraryEmpty` を公開する
```csharp
// ViewModel側
public bool IsQueueEmpty => DownloadQueue.Count == 0;

// XAML側
Visibility="{Binding IsQueueEmpty, Converter={StaticResource BoolToVis}}"
```

**5. 注意点**
表示だけの変更なので副作用は小さいです。ただし `ObservableCollection.Count` の変化通知は自動では飛ばないため、`CollectionChanged` で `OnPropertyChanged(nameof(IsQueueEmpty))` を呼ぶ必要があります。

---

## 【低】12. 設定画面でツールパスを手入力したとき、検証状態が即時更新されない

**1. 改善すべき点**
[SettingsView.xaml:65](YouTubeDownloader/Views/SettingsView.xaml#L65) は `UpdateSourceTrigger=PropertyChanged` で入力値は即時反映されますが、パスの妥当性検証（[SettingsViewModel.cs:219](YouTubeDownloader/ViewModels/SettingsViewModel.cs#L219) `ValidatePaths`）は Browse ボタン押下後やロード時にしか走りません。

**2. 優先度**：低

**3. 理由**
手入力した有効パスで検証状態が「見つかりません」のまま、または無効パスなのに前回の「OK」表示が残る、という UX のズレが起きます。

**4. 具体的な対応案**
`OnYtDlpPathChanged` / `OnFfmpegPathChanged` の partial メソッドで検証を更新します。
```csharp
partial void OnYtDlpPathChanged(string value)
{
    IsYtDlpValid = !string.IsNullOrEmpty(value) && File.Exists(value);
    // 空欄時のみ自動検出を更新
    if (string.IsNullOrEmpty(value)) AutoDetectPaths();
}
```
PATH 自動検出は毎キー入力で走らせず、手入力パスの存在確認だけにすると応答性が良いです。

**5. 注意点**
空欄は自動検出を使う仕様（L71-72 のヒント文言）に見えるため、「手入力パスの妥当性」と「自動検出で利用可能なツール」の表示を混ぜない方がよいです。

---

## 【低】13. `GetThumbnailUrl` が最大解像度サムネを選び、一覧表示に過大画像を読む

**1. 改善すべき点**
[YtDlpClient.cs:264-295](YouTubeDownloader/Services/YtDlpClient.cs#L264) の `GetThumbnailUrl` は「最大 width」のサムネを選びます。プレイリストの各行や解析プレビューの小さなサムネ表示に対し、`maxresdefault`（1280×720 以上、数百KB〜MB）を毎回ダウンロードすることになり、行数が多いと帯域・メモリの無駄になります。

**2. 優先度**：低

**3. 理由**
表示は小サムネなので、過大解像度は読み込み遅延・メモリ増の割に画質メリットがありません。

**4. 具体的な対応案**
表示用は中庸サイズを選ぶ（例：width が一定値以下で最大のもの、または yt-dlp の `mqdefault`/`hqdefault` 相当を優先）。実装が重ければ現状維持でも可。

**5. 注意点**
WPF の `Image` 側で `DecodePixelWidth` を指定してデコード解像度を抑えるだけでもメモリは大きく削減できます（URL選定を変えなくてもよい）。

---

## 【低】14. `ValidatePaths`/`AutoDetectPaths` が UI スレッドで同期ディスク探索

**1. 改善すべき点**
[SettingsViewModel.cs:219-261](YouTubeDownloader/ViewModels/SettingsViewModel.cs#L219) の `ValidatePaths → AutoDetectPaths` は、`ExecutableLocator.FindExecutable`（PATH 全走査＋多数の共通パス＋アプリディレクトリの `File.Exists`）を UI スレッドで同期実行します。設定ロード時・パス参照ボタン押下時に走ります。

**2. 優先度**：低

**3. 理由**
頻度は低いものの、PATH にネットワークドライブや切断中ドライブが含まれると `File.Exists` がタイムアウトし、設定画面を開いた瞬間に UI が固まる可能性があります。

**4. 具体的な対応案**
自動検出を `Task.Run` でバックグラウンド実行し、結果プロパティだけ UI スレッドへ反映。検出結果は短時間キャッシュして連続呼び出しを抑制（前回指摘14のキャッシュ方針と整合）。

**5. 注意点**
非同期化すると検出表示にわずかな遅延が出ます。実害が小さいので「気づいたら直す」レベルで可。

---

## 【低】15. 24時間以上の動画で再生時間表示がずれる

**1. 改善すべき点**
[VideoMetadata.cs:51-54](YouTubeDownloader/Models/VideoMetadata.cs#L51) の `DurationFormatted` は `ts.Hours` を使っていますが、`TimeSpan.Hours` は 0〜23 の範囲しか返しません。**25時間の動画は `1:00:00` と表示されます**。

**2. 優先度**：低

**3. 理由**
YouTube には24時間超のライブ配信アーカイブが存在するため、発生自体はあり得ます。ただし対象動画は限定的です。

**4. 具体的な対応案**
```csharp
var hours = (int)ts.TotalHours;
return hours > 0
    ? $"{hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
    : $"{ts.Minutes}:{ts.Seconds:D2}";
```

**5. 注意点**
表示だけの修正で副作用なし。長時間配信を対象外にする仕様なら優先度はさらに下がります。

---

## 【低】16. `ClearAll` 後も in-flight ジョブが完走するとライブラリへ保存され得る（エッジ）

**1. 改善すべき点**
[DownloadManager.cs:215](YouTubeDownloader/Services/DownloadManager.cs#L215) の `ClearAll` は `CancelAll()` 後に `_allJobs.Clear()` しますが、既に実行中の `ProcessJobAsync` はキャンセルが伝わる前にダウンロードを完走し得ます。その場合 `SaveVideoMetadataAsync` が走り、「クリアしたはずの動画」がライブラリ履歴に残ることがあります。

**2. 優先度**：低（タイミング依存のエッジ）

**3. 理由**
キャンセルは協調的（プロセス kill→OCE 伝播）なので、完了直前にクリアすると「クリアしたのに履歴に出る」軽微な不整合が起こり得ます。

**4. 具体的な対応案**
完了時の保存前に `cts.Token.IsCancellationRequested` を確認して保存をスキップする、もしくは現状仕様（「ダウンロードできたものは履歴に残す」）として許容する。**どちらを正とするか確認が必要**です。

**5. 注意点**
実害は小さく、無理に直すとロジックが複雑化します。現行が許容範囲なら据え置きで問題ありません。

---

### まとめ

| 優先度 | 指摘 | 修正コスト | 方針確認要否 |
|--------|------|-----------|-------------|
| 中-高 | 1. ファイル名テンプレートのデータ損失リスク | 低 | △（既存ユーザー） |
| 中 | 2. LocalFilePath 特定の曖昧さ | 中 | ✗ |
| 中 | 3. 解析処理の多重実行防止 | 低 | ✗ |
| 中 | 4. 進捗パースのカルチャ依存 | 低 | ✗ |
| 中 | 5. 更新プロセスのキャンセル非対称 | 低 | ○ |
| 中 | 6. RunYtDlpAsync の exit code/stderr 破棄 | 中 | ✗ |
| 中 | 7. VM のライフサイクルとイベント購読 | 低 | ✗ |
| 中 | 8. ライブラリ自動反映なし | 中 | ✗ |
| 低 | 9-16（8件） | 低〜中 | 一部要 |

- **即効性が高く修正コストも低い**のは **指摘1（テンプレート防御）・指摘3（多重実行防止）・指摘4（カルチャ依存）** です。
- **設計的に効いてくる**のは **指摘7（VM のライフサイクル）と指摘8（ライブラリ自動反映）** で、この2つは一緒に方針を決めると整理しやすいです。
- **指摘5・9・16** は仕様意図の確認が必要な項目です（更新キャンセルの扱い／完了通知の定義／クリア後の保存）。
- **指摘2（LocalFilePath）と指摘6（stderr）** は中程度の修正コストですが、UX 改善効果が大きいため余力があれば着手推奨です。

着手してよいものを指定いただければ、既存動作を保つ形で該当箇所のみのパッチを作成します。
