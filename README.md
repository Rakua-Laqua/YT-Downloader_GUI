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
- .NET 8.0 Runtime
- [yt-dlp](https://github.com/yt-dlp/yt-dlp/releases) - 動画ダウンロードエンジン
- [ffmpeg](https://ffmpeg.org/download.html) - 動画/音声変換

## インストール

1. [Releases](../../releases)から最新版をダウンロード
2. 任意のフォルダに展開
3. `yt-dlp.exe` と `ffmpeg.exe` をダウンロードして配置
4. アプリを起動し、設定画面でyt-dlpとffmpegのパスを設定

## 使い方

1. **設定**: 初回起動時に設定画面でyt-dlpとffmpegのパスを指定
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

## 貢献

Issue、Pull Requestは歓迎します！
