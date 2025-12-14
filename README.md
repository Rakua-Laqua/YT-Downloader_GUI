# YouTube Downloader

Windows用のYouTube動画ダウンローダーアプリケーションです。
WPF + .NET 8 + MVVMアーキテクチャで構築されています。

## 機能

- 🎬 **動画ダウンロード**: YouTube動画を高品質でダウンロード
- 🎵 **音声抽出**: 動画から音声のみを抽出（MP3, M4A, WAV）
- 📋 **プレイリスト対応**: プレイリスト全体または選択した動画をダウンロード
- 📁 **自動整理**: プレイリスト名でフォルダを自動作成
- 🏷️ **メタデータ埋め込み**: タイトル、アーティスト、サムネイルを自動埋め込み
- 📚 **ライブラリ管理**: ダウンロード履歴の検索・管理

## スクリーンショット

（スクリーンショットをここに追加）

## 必要条件

- Windows 10 / 11
- [yt-dlp](https://github.com/yt-dlp/yt-dlp/releases) - 動画ダウンロードエンジン
- [ffmpeg](https://ffmpeg.org/download.html) - 動画/音声変換

※ 配布版（自己完結 / self-contained）を使う場合、.NET Runtime の別途インストールは不要です。

## インストール

1. [Releases](../../releases)から最新版をダウンロード
2. 任意のフォルダに展開
3. `yt-dlp.exe` と `ffmpeg.exe` をユーザー側で用意（どちらも任意の場所でOK）
  - 例: PATHが通っている場所にインストール（推奨）
4. アプリを起動
  - 設定画面のパス欄が空でも、自動検出できればそのまま動作します
  - 自動検出できない場合のみ、設定画面でパスを指定してください

## 使い方

1. **設定**: 初回起動時に設定画面で状態を確認（必要ならパスを指定）
2. **URL入力**: ダウンロード画面でYouTube URLを入力し「解析」をクリック
3. **設定選択**: 
   - ダウンロード種別（動画+音声 / 音声のみ）
   - フォーマット（mp4, mkv, mp3など）
   - 品質（best, 1080p, 720pなど）
4. **ダウンロード**: 「ダウンロード開始」をクリック

## ビルド方法

```bash
# リポジトリをクローン
git clone https://github.com/Rakua-Laqua/YT-Downloader_GUI.git
cd YT-Downloader_GUI

# ビルド
dotnet build

# 実行
dotnet run --project YouTubeDownloader
```

## 配布（publish）

配布用には `dotnet publish` を使って「実行に必要なファイル一式」を出力します。

### おすすめ（自己完結 / .NET不要）

配布先PCに .NET のインストールが不要な方式です（ファイルサイズは大きめ）。

```powershell
./publish.ps1 -Runtime win-x64
```

※ 環境によっては PowerShell の実行ポリシーで `publish.ps1` が実行できない場合があります。
その場合は、同等のコマンドを直接実行してください。

```powershell
dotnet publish "YouTubeDownloader/YouTubeDownloader.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "artifacts/publish/win-x64/self-contained"
```

生成物:

- `artifacts/publish/win-x64/self-contained/YouTubeDownloader.exe`
- `artifacts/dist/YouTubeDownloader_win-x64_self-contained_*.zip`

### 軽量（Framework Dependent / .NETが必要）

配布先PCに **.NET 8 Desktop Runtime** が必要ですが、配布物が軽くなります。

```powershell
./publish.ps1 -Runtime win-x64 -FrameworkDependent
```

### yt-dlp / ffmpeg の同梱について

このアプリは `yt-dlp.exe` と `ffmpeg.exe` を以下の順で探します。

1. 設定で指定したパス
2. PATH
3. 一般的なインストール場所
4. アプリと同じフォルダ

今回の配布方針は **同梱しない** ため、利用者が各自で `yt-dlp` / `ffmpeg` をインストールし、
PATH を通すか設定画面でパスを指定してください。

## 技術スタック

- **フレームワーク**: .NET 8 / WPF
- **アーキテクチャ**: MVVM
- **主要ライブラリ**:
  - CommunityToolkit.Mvvm
  - Microsoft.Extensions.DependencyInjection
  - System.Text.Json
- **外部ツール**:
  - yt-dlp
  - ffmpeg

## プロジェクト構造

```
YouTubeDownloader/
├── Models/           # データモデル
├── Services/         # ビジネスロジック
├── ViewModels/       # MVVM ViewModel
├── Views/            # WPF Views (XAML)
├── Converters/       # 値コンバーター
└── Infrastructure/   # インフラストラクチャ
```

## ライセンス

MIT License

## 注意事項

- このアプリケーションは個人使用を目的としています
- YouTubeの利用規約を遵守してご使用ください
- 著作権で保護されたコンテンツのダウンロードは、著作権者の許可がある場合にのみ行ってください

### 既知の注意（yt-dlp）

- 環境によっては `yt-dlp` 実行時に「JavaScript runtime が見つからない」警告が出ることがあります（形式が欠ける場合があります）。その場合は yt-dlp の案内に従って JS runtime（例: deno 等）を導入してください。

## 貢献

Issue、Pull Requestは歓迎します！
