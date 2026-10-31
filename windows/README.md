# Library Server for Windows

`library.php` (PHP 版ライブラリ API) を **PHP 不要の Windows アプリ 1 本**に置き換えたものです。
ビューア本体 (`comic-viewer.html` / `pdf-viewer.html`) も同梱して配信するので、これだけで

- PC のブラウザ、同じ Wi-Fi のスマートフォン / タブレット (PWA) から、指定フォルダの本を一覧・検索して開ける
- 表紙画像 (サイドカー) を**自動生成**する (PDF / EPUB / 書庫。外部コマンド不要)
- トレイに常駐し、設定はウィンドウ内の画面で行える。ウィンドウ内でそのまま読める

A Windows replacement for `library.php`: a single tray-resident executable that serves the viewer and the
library API (fully compatible with `library.php`), generates cover images automatically, and needs no PHP,
Apache or ImageMagick. Phones on the same Wi-Fi use the existing PWA unchanged.

- 配布サイト / Project page: <https://github.com/id-fa/simple-pdf-viewer-with-screenshot>
- ダウンロード / Download: [Releases](https://github.com/id-fa/simple-pdf-viewer-with-screenshot/releases) の `LibraryServer-win-x64.zip`
  (`LibraryServer.exe` / `LibraryServer.Host.exe` / `README.md` / `LICENSE` 入り)
- ビューア本体 (GitHub Pages 版) / Hosted viewer: <https://id-fa.github.io/simple-pdf-viewer-with-screenshot/webapp/comic-viewer.html>

## 構成 / What's inside

| ファイル | 用途 |
|---|---|
| `LibraryServer.exe` | トレイアプリ (推奨)。サーバー + 設定画面 + ウィンドウ内ビューア |
| `LibraryServer.Host.exe` | ヘッドレス版。コンソール / Windows サービス / 表紙生成 CLI (`covers`) |

どちらも自己完結の単一 exe (.NET ランタイム同梱) で、インストーラは不要です。
ウィンドウ内ビューアには **Microsoft Edge WebView2 ランタイム**が必要ですが、Windows 10/11 には通常入っています
(無い場合もサーバー機能とブラウザからの利用はそのまま動きます)。

## はじめかた / Getting started

1. [Releases](https://github.com/id-fa/simple-pdf-viewer-with-screenshot/releases) から `LibraryServer-win-x64.zip` を取得して展開し、`LibraryServer.exe` を任意のフォルダに置いて起動する
2. 初回は設定画面が開くので「公開フォルダ」(本の入ったフォルダ) を選んで **保存**
3. Windows ファイアウォールのダイアログが出たら「プライベート ネットワーク」で許可する
4. 「状態」タブに出ている URL をブラウザで開く。スマートフォンは LAN の URL (QR コードあり) を開き、
   そのままホーム画面に追加 (PWA) できる
5. 必要なら「Windows 起動時に開始」をオンにする (トレイメニュー / 設定画面)

設定ファイルは `%LocalAppData%\LibraryServer\library.config.json` に保存されます
(exe と同じフォルダに `library.config.json` を置けばポータブル運用)。

## トレイメニュー

| 項目 | 動作 |
|---|---|
| ビューアを開く (ダブルクリックも同じ) | ウィンドウ内でコミックビューアを開く |
| ブラウザで開く | 既定のブラウザで `comic-viewer.html` を開く |
| 設定… | 設定画面 (状態 / 設定 / 表紙 / ログ) |
| 表紙を生成 | 不足分だけ / すべて作り直す |
| Windows 起動時に開始 | サインイン時にトレイへ常駐 (`HKCU\...\Run`) |
| 終了 | サーバーを止めて終了 (ウィンドウの × は隠すだけ) |

## 設定 / Configuration

`library.config.json` のキーは `library.config.php` と同じ名前 (camelCase) です。値はそのまま移せます。

| キー | 既定 | 説明 |
|---|---|---|
| `root` | `""` | 公開するフォルダの絶対パス。空なら「未設定」(API は 503) |
| `label` | `""` | 一覧のルート表示名 (空なら `root` のフォルダ名) |
| `auth` | `null` | `{ "user": "...", "pass": "...", "realm": "Library" }`。`pass` は平文でも bcrypt (`$2y$…`、PHP の `password_hash()` 出力) でも可。設定画面から保存すると bcrypt 化される |
| `exts` | pdf, cbz, cbr, cb7, epub, zip, rar, 7z | 配信する拡張子 |
| `coverSuffix` / `coverExts` | `.coverimage` / webp, avif, png, jpg, jpeg, gif | 表紙サイドカーの命名規則 |
| `maxEntries` / `maxDepth` / `followSymlinks` | 5000 / 12 / false | 走査の上限 |
| `port` | 8321 | 待ち受けポート |
| `bind` | `lan` | `lan` = 全インターフェース / `localhost` = この PC だけ |
| `coi` | false | 全レスポンスに COOP / COEP を付ける (wasm-vips 用) |
| `covers` | 下記 | 表紙生成 |
| `autoStart` | true | (設定画面の表示用) |

`covers`: `auto` (起動時 + 監視で自動生成) / `format` (webp \| jpeg \| png) / `quality` / `maxWidth` / `maxHeight` /
`sort` (lexical \| natural) / `epubCover` (spine \| metadata) / `matchMtime` / `pdfDpi` / `maxCandidates` / `passthrough`
— `tools/generate_coverimages.*` の `coverTool` と同じ意味です。

`fsEncoding` はありません (.NET はファイル名を常に Unicode で扱います)。

## 表紙画像 / Cover images

- 命名・決め方は PHP / Python 版ツールと同じ: `vol01.cbz` → `vol01.cbz.coverimage.webp`。PDF は 1 ページ目 (PDFium)、
  EPUB は読み順の 1 枚目 (構造解析)、書庫はファイル名順の 1 枚目
- `covers.auto` が有効なら、起動時に「無い / 元ファイルより古い」表紙を作り、以後はフォルダ監視でファイルの追加・更新を
  検知して 1 件ずつ作ります (コピー途中のファイルはサイズが安定するまで待ちます)
- コマンドラインからも作れます (オプションは Python 版と同じ):

```bat
LibraryServer.Host.exe covers --check
LibraryServer.Host.exe covers --dry-run -v
LibraryServer.Host.exe covers --stale --mtime
LibraryServer.Host.exe covers --force --format=jpeg --quality=85 --path=Manga
LibraryServer.Host.exe covers --help
```

## ヘッドレス / Windows サービス

GUI 無しで動かす場合は `LibraryServer.Host.exe` を使います。起動時に設定画面の URL (トークン付き) がログに出ます。

```bat
LibraryServer.Host.exe --headless --root=D:\Books --port=8321
LibraryServer.Host.exe --headless --config=C:\path\to\library.config.json
```

Windows サービスとして登録する例 (管理者のコマンドプロンプト。設定は `%ProgramData%\LibraryServer\library.config.json` に置く):

```bat
mkdir "%ProgramData%\LibraryServer"
copy "%LocalAppData%\LibraryServer\library.config.json" "%ProgramData%\LibraryServer\"
sc create LibraryServer binPath= "\"C:\Apps\LibraryServer\LibraryServer.Host.exe\" --service" start= auto DisplayName= "Library Server (Comic Viewer)"
sc start LibraryServer
```

サービスはログオンしていなくても動きますが、トレイアイコンとウィンドウ内ビューアはありません (ブラウザから使います)。

## ファイル関連付け

エクスプローラーで `.cbz` 等を右クリック →「プログラムから開く」→ `LibraryServer.exe` を選ぶと、
そのファイルをウィンドウ内のビューアで開きます (既に起動していればそのウィンドウに渡します)。
公開フォルダの中のファイルはライブラリ経由 (`?lib=`、しおりも共有)、外のファイルは一時 URL (`/__local/<token>`、
この PC 内からのみ 30 分有効) 経由で開きます。

## ビルド / Building

.NET 8 SDK が必要です (`winget install Microsoft.DotNet.SDK.8`)。

```bat
cd windows
dotnet build
dotnet test
rem 配布用 (単一 exe、約 90MB / 60MB)
dotnet publish src\LibraryServer.App\LibraryServer.App.csproj  -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish\app
dotnet publish src\LibraryServer.Host\LibraryServer.Host.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish\host
```

| プロジェクト | 内容 |
|---|---|
| `src/LibraryServer.Core` | 設定、索引 (走査)、Basic 認証、表紙生成 (PDFium / SharpCompress / SkiaSharp)。UI / HTTP 非依存 |
| `src/LibraryServer.Server` | Kestrel ホスト: `/library.php` 互換 API、静的配信 (ビューア一式を埋め込み)、管理 API (`/__admin/*`)、管理画面 (`wwwroot/__app/`)、表紙の自動生成サービス |
| `src/LibraryServer.Host` | ヘッドレス exe (`--headless` / `--service` / `covers`) |
| `src/LibraryServer.App` | トレイアプリ (WinForms + WebView2) |
| `tests/LibraryServer.Tests` | xunit (API 互換、パス検証、表紙生成) |

ビューアの HTML / `sw.js` / `vendor/` はリポジトリのルートから**ビルド時に埋め込む**ので二重管理はありません
(exe の隣に `wwwroot/` フォルダを置くとそちらが優先されます。開発時のホットリロード用)。

## 設計メモ / Design notes

- **API は `library.php` と同じ URL・同じレスポンス**。`comic-viewer.html` / `pdf-viewer.html` / `sw.js` は無改修で動きます
  (`ping` / `tree` / `file` (Range 対応) / `cover` (ETag / 304、`w=` で縮小))
- **`file` / `cover` の `path` はメモリ上の索引に存在するものしか解決しません**。`..` やドット始まりに加え、Windows 固有の
  8.3 短縮名 (`FOO~1.PDF`)、代替データストリーム (`book.cbz:x`)、デバイス名 (`CON`) もまとめて 404 になります
- 管理 API (`/__admin/*`) はループバックからの接続 + 起動ごとの乱数トークンが必要です。管理画面 (`/__app/*`) と一時ファイル
  配信 (`/__local/*`) はループバック以外には 404。`sw.js` は `/__` 始まりのパスをキャッシュしません
- 表紙の縮小結果 (`cover&w=`) は `covercache/` にディスクキャッシュします (PHP 版は毎回縮小)

## 制限 / Limitations

- LAN への配信は HTTP 平文です。Basic 認証は資格情報を毎回 base64 で送るだけなので、LAN の外に公開するなら HTTPS の
  リバースプロキシ (Caddy / nginx / Cloudflare Tunnel 等) を前に置いてください
- iOS Safari は HTTP では Service Worker が動かないため、PWA としてのインストールやオフライン動作はできません
  (閲覧とファイルを開く操作は可能)
- AVIF / JXL / HEIC だけで構成された書庫の表紙は作れません (再エンコードせずそのまま置く `passthrough` なら可)
- これは DRM ではありません。表示できたファイルはビューアの保存機能でダウンロードできます
