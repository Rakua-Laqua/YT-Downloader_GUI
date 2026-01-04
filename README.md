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

<<<<<<< HEAD
### yt-dlp / ffmpeg の同梱について
=======
## 配布（publish）

配布用には `publish.ps1` を使います。用途に応じて3つのビルドパターンがあります。

| パターン | サイズ | .NET必要 | yt-dlp/ffmpeg |
|----------|--------|----------|---------------|
| ① 軽量版 | ~3MB | 要 | 別途用意 |
| ② 自己完結版 | ~150MB | 不要 | 別途用意 |
| ③ フル同梱版 | ~200MB | 不要 | 同梱 |

---

### ① 軽量版（本体のみ / Framework Dependent）

配布先PCに [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) が必要ですが、配布物が軽くなります。

```powershell
./publish.ps1 -Zip -Clean
```

出力:
- `artifacts/publish/win-x64/framework-dependent/`
- `artifacts/dist/win-x64/YouTubeDownloader-vXXX-win-x64-framework-dependent.zip`

---

### ② 自己完結版（.NET同梱 / Self-Contained）

配布先PCに .NET のインストールが不要です（.NETランタイムを同梱）。

```powershell
./publish.ps1 -Mode self-contained -Zip -Clean
```

出力:
- `artifacts/publish/win-x64/self-contained/`
- `artifacts/dist/win-x64/YouTubeDownloader-vXXX-win-x64-self-contained.zip`

---

### ③ フル同梱版（.NET + yt-dlp + ffmpeg）

すべて同梱した配布パッケージを作成します。  
`-IncludeTools` を付けると出力フォルダ名に `-with-tools` が付きます。  
**yt-dlp.exe / ffmpeg.exe は手動で出力フォルダに配置してから ZIP 化してください。**

```powershell
# 1. まず self-contained でビルド
./publish.ps1 -Mode self-contained -IncludeTools -Clean

# 2. 出力フォルダにツールを手動配置
#    artifacts/publish/win-x64/self-contained-with-tools/ に
#    yt-dlp.exe, ffmpeg.exe, ffprobe.exe をコピー

# 3. ZIP化
./publish.ps1 -Mode self-contained -IncludeTools -Zip
```

出力:
- `artifacts/publish/win-x64/self-contained-with-tools/`
- `artifacts/dist/win-x64/YouTubeDownloader-vXXX-win-x64-self-contained-with-tools.zip`

---

### publish.ps1 パラメータ一覧

| パラメータ | 値 | 説明 |
|-----------|-----|------|
| `-Configuration` | `Release` / `Debug` | ビルド構成（デフォルト: Release） |
| `-Runtime` | `win-x64` / `win-x86` / `win-arm64` | ターゲット（デフォルト: win-x64） |
| `-Mode` | `framework-dependent` / `self-contained` / `both` | ビルドモード（デフォルト: framework-dependent） |
| `-IncludeTools` | スイッチ | 出力名に `-with-tools` を付加 |
| `-Clean` | スイッチ | ビルド前に出力フォルダを削除 |
| `-Zip` | スイッチ | ZIP ファイルを作成 |

---

### yt-dlp / ffmpeg の探索順序
>>>>>>> b34eaf3dd2b462b77f070e728f1255ce3fc43b29

このアプリは `yt-dlp.exe` と `ffmpeg.exe` を以下の順で探します。

1. 設定画面で指定したパス
2. 環境変数 PATH
3. 一般的なインストール場所
4. アプリと同じフォルダ（同梱版向け）

軽量版・自己完結版を使う場合は、利用者が各自で `yt-dlp` / `ffmpeg` をインストールし、
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
