# YouTube Downloader

Windows 用の YouTube 動画・音声ダウンローダーです。WPF + .NET 8 + MVVM で構築しており、内部では `yt-dlp` と `ffmpeg` を呼び出します。

単一動画とプレイリストの解析、選択ダウンロード、キュー管理、ダウンロード履歴の検索・再生・フォルダー表示に対応しています。

## 主な機能

- 単一動画 / プレイリスト URL の解析
- プレイリスト内動画の選択、全選択、全解除
- 動画ダウンロード: `mp4`, `mkv`, `webm`
- 音声抽出: `mp3`, `m4a`, `wav`
- 品質選択: `best`, `1080p`, `720p`, `480p`, `360p`
- 音声品質プリセット: VBR 0 / 2 / 5 / 7 / 10、`128K`, `192K`, `256K`
- 同時ダウンロード数の設定: 1 から 8
- ダウンロードキューのキャンセル、リトライ、全キャンセル、完了済みクリア
- プレイリスト名での保存フォルダー自動作成
- タイトル取得言語の指定: `default`, `ja`, `en` など
- ファイル名テンプレート指定
- メタデータ埋め込み、音声ファイルへのサムネイル埋め込み
- ファイル更新日時を動画の公開時刻へ合わせる設定
- メタデータの「年」タグを公開年へ修正する設定
- yt-dlp の自動更新、手動更新、stable / nightly チャンネル切り替え
- cookies.txt を使った認証(年齢制限・メンバー限定・非公開動画やbot検知対策、任意設定)
- プレイリスト取得漏れの警告表示
- ダウンロード失敗時の詳細コピー
- 実行ログの保存

## 必要条件

- Windows 10 / 11
- `yt-dlp.exe`
- `ffmpeg.exe`

配布版のうち `framework-dependent` を使う場合は、別途 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) が必要です。`self-contained` 版では .NET Runtime の別途インストールは不要です。

`deno` または `node` がインストールされている環境では、yt-dlp の JavaScript チャレンジ解決に使われる場合があります。必須ではありませんが、動画によっては導入すると取得できる形式が増えることがあります。

## インストール

1. [Releases](../../releases) から最新版をダウンロードします。
2. ZIP を任意のフォルダーへ展開します。
3. `YouTubeDownloader.exe` を起動します。
4. 設定画面で `yt-dlp` と `ffmpeg` の状態が `OK` になっているか確認します。

`yt-dlp.exe` と `ffmpeg.exe` は、設定画面で直接パスを指定できます。パス欄が空の場合は自動検出を使います。

自動検出では、次の順に実行ファイルを探します。

1. 環境変数 `PATH`
2. WinGet / Program Files / Scoop / `C:\tools` などの一般的な場所
3. アプリ本体と同じフォルダー

配布物に `yt-dlp.exe` / `ffmpeg.exe` を同梱する場合は、アプリ本体と同じフォルダーに配置してください。

## 使い方

1. 設定画面で外部ツール、保存先、既定フォーマット、同時ダウンロード数などを確認します。
2. ダウンロード画面で YouTube の動画またはプレイリスト URL を入力し、`解析` を押します。
3. プレイリストの場合は、ダウンロードしたい動画を選択します。
4. 保存先、ダウンロード種別、フォーマット、品質を選びます。
5. `ダウンロード開始` を押します。

ダウンロード中の項目はキューに表示されます。失敗した項目は `リトライ` できます。詳細なエラーがある場合は `失敗詳細をコピー` から、失敗フェーズ、終了コード、実行コマンド、stderr などをコピーできます。

## ライブラリ

ダウンロードが完了した動画は、ライブラリに履歴として保存されます。ライブラリでは次の操作ができます。

- タイトル / チャンネル名で検索
- ファイルを再生
- 保存フォルダーを開く
- YouTube の動画ページを開く
- 履歴から削除
- 複数選択して履歴から一括削除

ライブラリの削除操作は履歴だけを削除します。ダウンロード済みの実ファイルは削除しません。

## 設定

設定画面では、主に次の項目を変更できます。

- `yt-dlp` / `ffmpeg` の実行ファイルパス
- yt-dlp の自動更新
- yt-dlp の更新チャンネル: `stable` / `nightly`
- 既定の保存先フォルダー
- タイトル取得言語
- 既定の動画 / 音声フォーマット
- 既定の動画 / 音声品質
- 同時ダウンロード数
- mp4 保存時に AV1 などの高効率コーデックを優先するか
- ファイル更新日時を動画の公開時刻に合わせるか
- メタデータの「年」を公開年に修正するか
- ファイル名テンプレート
- 認証用 cookies.txt のパス(任意設定。年齢制限・メンバー限定・非公開動画や、一時的なbot検知が発生した動画の取得・解析に利用)

ファイル名テンプレートでは次のプレースホルダーを使用できます。

| プレースホルダー | 内容 |
|---|---|
| `{title}` | 動画タイトル |
| `{channel}` | チャンネル名 |
| `{id}` | 動画 ID |
| `{index}` | プレイリスト内の番号(2 桁のゼロ埋め表記。`{index:02d}` も同じ結果になります) |

拡張子を指定しない場合は、yt-dlp の出力拡張子 `%(ext)s` が自動で付きます。絶対パスや `..` で保存先フォルダー外を指すテンプレートはエラーになります。

## 保存されるデータ

設定、履歴、ログはユーザーのローカルアプリデータ配下に保存されます。

| 種類 | 場所 |
|---|---|
| 設定 | `%LocalAppData%\YouTubeDownloader\settings.json` |
| ライブラリ履歴 | `%LocalAppData%\YouTubeDownloader\metadata.json` |
| ログ | `%LocalAppData%\YouTubeDownloader\logs\app-yyyyMMdd.log` |

ログは日付別に出力され、古いログは自動で整理されます。

## ビルド方法

```powershell
git clone https://github.com/Rakua-Laqua/YT-Downloader_GUI.git
cd YT-Downloader_GUI

dotnet restore .\YouTubeDownloader\YouTubeDownloader.csproj
dotnet build .\YouTubeDownloader\YouTubeDownloader.csproj -c Debug
dotnet run --project .\YouTubeDownloader\YouTubeDownloader.csproj
```

Release ビルドだけを確認する場合は次のコマンドを使います。

```powershell
dotnet build .\YouTubeDownloader\YouTubeDownloader.csproj -c Release
```

## 配布ビルド

配布用の成果物は `dotnet publish` で作成します(ビルド・リリース補助スクリプト `publish.ps1` / `release.ps1` は v3.7.0 で削除済みです)。

| モード | 内容 | `--self-contained` |
|---|---|---|
| framework-dependent | .NET Runtime を同梱しない軽量版 | `false` |
| self-contained | .NET Runtime を同梱する単体実行版 | `true` |

```powershell
# 軽量版
dotnet publish .\YouTubeDownloader\YouTubeDownloader.csproj -c Release -r win-x64 --self-contained false -o artifacts\publish\win-x64\framework-dependent

# 自己完結版
dotnet publish .\YouTubeDownloader\YouTubeDownloader.csproj -c Release -r win-x64 --self-contained true -o artifacts\publish\win-x64\self-contained
```

`-r` には `win-x64` / `win-x86` / `win-arm64` などのランタイム識別子を指定できます。

`yt-dlp.exe` / `ffmpeg.exe` / `ffprobe.exe` を同梱する場合は、`dotnet publish` の出力フォルダーへ手動でコピーしたうえで ZIP 化してください。

GitHub Release の作成は、[Releases](../../releases) ページから手動で行うか、`gh` CLI で作成してください。

```powershell
gh release create v<version> <作成したzipファイルのパス> --title "v<version>" --generate-notes
```

## 技術スタック

- .NET 8 / WPF
- MVVM
- CommunityToolkit.Mvvm
- Microsoft.Extensions.DependencyInjection
- System.Text.Json
- yt-dlp
- ffmpeg

## プロジェクト構造

```text
YouTubeDownloader/
├── Models/            # 設定、動画、プレイリスト、ジョブなどのモデル
├── Services/          # 設定保存、履歴保存、ログ、yt-dlp連携、ダウンロード管理
│   └── YtDlp/         # yt-dlpの解析、更新、引数構築、実行、失敗詳細整形
├── ViewModels/        # 画面ごとのViewModel
├── Views/             # WPF View
├── Converters/        # XAML用コンバーター
└── Infrastructure/    # 外部リンク、タスクバー通知、サムネイル変換など
```

## 注意事項

- このアプリケーションは個人使用を目的としています。
- YouTube の利用規約を遵守して使用してください。
- 著作権で保護されたコンテンツは、権利者の許可がある場合にのみダウンロードしてください。
- yt-dlp の仕様変更や YouTube 側の変更により、一時的に解析・ダウンロードできない場合があります。その場合は yt-dlp の更新、`nightly` チャンネルへの切り替え、`deno` / `node` の導入を試してください。

## ライセンス

MIT License
