
前回（評価内容.md）でカバーされている指摘、および直近の差分で**指摘1・2・3が既に修正済み**であることを確認しました。その上で、**前回挙がっていない新しい観点**だけを以下にまとめます。

なお重大な機能不全（キャンセルでプロセスが残る等）は既に対処済みのため、今回は中〜低優先度の論理穴・ロケール依存・保守性・拡張性が中心です。

---

## 【中】1. 進捗パースの `double.TryParse` がカルチャ非依存でなく、一部ロケールで進捗が壊れる

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

## 【中】2. 自動更新／解析プロセスがキャンセル時に kill されない（download path との非対称）

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

## 【中】3. `DownloadViewModel`（Transient）が Singleton の `DownloadManager` イベントを購読し、解除しない

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

## 【中】4. ライブラリがダウンロード完了を自動反映しない

**1. 改善すべき点**
`LibraryViewModel` は `DownloadManager` のイベントを購読していません。ダウンロード完了で `MetadataRepository` に履歴が追加されますが、**既にライブラリ画面を開いている状態では一覧が更新されません**（再ナビゲーション or 更新ボタンが必要）。

**2. 優先度**：中（機能の自然さに関わる）

**3. 理由**
「ダウンロード→ライブラリで確認」という基本動線で、ユーザーが「保存されていない？」と誤解しやすい挙動です。

**4. 具体的な対応案**
`LibraryViewModel` で `JobStatusChanged` を購読し、`Completed` 到達時に該当アイテムを `Items` に追加（または `RefreshAsync` を間引き実行）。Dispatcher マーシャルは `DownloadViewModel` 同様に。指摘3で Singleton 化する場合は購読のライフサイクルも揃えてください。

**5. 注意点**
購読する場合は指摘3の購読解除問題が同じく付きまといます。常時 `RefreshAsync` は全件再読込なので、完了1件ごとの差分追加にする方が効率的です。

---

## 【低】5. `MaybeFlashWhenAllCompleted` が Canceled/Failed 混在時に永遠に発火しない

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

## 【低】6. 設定画面のファイル名プレビューが拡張子 `.mp4` 固定

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

## 【低】7. `GetThumbnailUrl` が最大解像度サムネを選び、一覧表示に過大画像を読む

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

## 【低】8. `ValidatePaths`/`AutoDetectPaths` が UI スレッドで同期ディスク探索

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

## 【低】9. `ClearAll` 後も in-flight ジョブが完走するとライブラリへ保存され得る（エッジ）

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

- 即効性が高く修正コストも低いのは **指摘1（カルチャ依存の進捗パース）** と **指摘6（プレビュー拡張子）**。
- 設計的に効いてくるのは **指摘3（VM のライフサイクルとイベント購読）** と **指摘4（ライブラリ自動反映）** で、この2つは一緒に方針を決めると整理しやすいです。
- **指摘2・5・9** は仕様意図の確認が必要な項目です（更新キャンセルの扱い／完了通知の定義／クリア後の保存）。

着手してよいものを指定いただければ、既存動作を保つ形で該当箇所のみのパッチを作成します。