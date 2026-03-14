# Comic Viewer

ブラウザベースの汎用コミックビューア。PDF / CBZ / CBR / CB7 / EPUB に対応。単一HTMLファイルで完結。

Created by id-fa, built with Claude Code.

## 対応形式

| 形式 | 拡張子 | ライブラリ |
|------|--------|-----------|
| PDF | `.pdf` | [PDF.js](https://mozilla.github.io/pdf.js/) v4.9.155 |
| CBZ | `.cbz`, `.zip` | [libarchive.js](https://github.com/nika-begiashvili/libarchivejs) v2.0.2 (WASM) |
| CBR | `.cbr`, `.rar` | libarchive.js v2.0.2 (WASM) |
| CB7 | `.cb7`, `.7z` | libarchive.js v2.0.2 (WASM) |
| EPUB | `.epub` | libarchive.js v2.0.2 (WASM) ※固定レイアウト(画像ベース)のみ |

アーカイブ内の画像ファイル (JPEG, PNG, WebP, GIF, BMP, AVIF, JXL, TIFF) を自動検出して表示します。

> **EPUB について**: EPUB は内部的に ZIP 形式であるため、アーカイブとして展開し画像ファイルを表示します。固定レイアウト(各ページが画像で構成されている)の EPUB のみ対応しています。テキストベース(リフロー型)の EPUB には対応していません。リフロー型 EPUB には [BiBI](https://id-fa.github.io/bibi-extension-ImageExporter/DEMO/) をお試しください。

## 使い方

### 起動

`file://` プロトコルでは WASM Worker が動作しないため、ローカル HTTP サーバーが必要です。

```bash
# Python
cd pdf-viewer-with-screenshot
python -m http.server 8000

# PHP
php -S localhost:8000

# Node.js (npx)
npx serve .
```

ブラウザで `http://localhost:8000/comic-viewer.html` を開きます。

### ファイルを開く

- 「Open」ボタンでファイルを選択
- ファイルをページにドラッグ&ドロップ

### ビューア操作

| 操作 | 説明 |
|------|------|
| `<` / `>` ボタン | ページ送り |
| ページ番号入力 | 任意ページにジャンプ |
| Single / Spread | 単ページ / 見開き切替 |
| Right (R2L) / Left (L2R) | 綴じ方向 (右綴じ=日本漫画, 左綴じ=洋書) |
| Cover | 表紙を単独ページとして扱う |
| HQ | PDF縮小表示の高品質モード (1xレンダリング→段階的半減縮小) |
| 50% ~ 200% / Fit | 表示スケール |
| Thumbs | サムネイルサイドバー表示 |

### キーボード / タップ操作

| 操作 | R2L (右綴じ) | L2R (左綴じ) |
|------|-------------|-------------|
| ← / 画面左端タップ | 次ページ | 前ページ |
| → / 画面右端タップ | 前ページ | 次ページ |
| ↑ | 前ページ | 前ページ |
| ↓ | 次ページ | 次ページ |
| Home | 最初のページ | 最初のページ |
| End | 最後のページ | 最後のページ |

画面の左右1/3エリアをクリック/タップでページ送りできます。中央1/3をタップするとUIの表示/非表示を切り替えます。

| 操作 | 説明 |
|------|------|
| 中央タップ / `H`キー | ヘッダーUIの表示/非表示を切替 |
| `Escape` | UIを再表示 |
| 左右スワイプ (タッチ) | ページ送り (スマートフォン対応) |

UI非表示時は表示スケール「Fit」がヘッダー分の領域も使って拡大表示されます。

### 画像エクスポート

| ボタン | 動作 |
|--------|------|
| Save Page | 現在のページ (見開き時は2ページ結合) を保存 |
| Save 2P | 現在ページ + 次ページの見開きを保存 |
| Save All | 全ページを連番で保存 |

出力形式は PNG / JPEG 95% / WebP 95% から選択可能。
- PDF: 2x スケールでレンダリング
- アーカイブ画像: ネイティブ解像度

### ソート順 (アーカイブ時のみ)

アーカイブ内の画像ファイル名の並び順を切り替えできます:

| ソート | 動作 | 例 |
|--------|------|-----|
| Natural (デフォルト) | 数字を数値として比較 | `img_1 → img_2 → img_3 → img_10 → img_100` |
| Lexical | 文字コード順 (辞書式) | `img_1 → img_10 → img_100 → img_11 → img_2` |
| Timestamp | ファイルの更新日時順 | 古い → 新しい (同一時刻は Natural) |

### 二重アーカイブ

CBZ 内に複数の CBZ/ZIP/RAR/7z が含まれている場合 (二重アーカイブ):

1. 外側アーカイブを展開し、内部アーカイブの一覧を表示
2. 展開したい内部アーカイブを1つ選択
3. 選択した内部アーカイブのみを展開・表示

全アーカイブを一度に展開しないため、大量のファイルを含む二重アーカイブでも効率的に動作します。

### ZIPファイル名エンコーディング修正

Windows で作成された ZIP/CBZ は、ファイル名が Shift-JIS (CP932) でエンコードされていることがあります。libarchive.js (WASM) はこれを UTF-8 として解釈するため文字化けが発生します。

この問題に対し、ZIP の中央ディレクトリを直接パースして正しいファイル名を復元します:

1. ZIPファイルの End of Central Directory Record (EOCD) を走査
2. 中央ディレクトリの各エントリからファイル名のバイト列を取得
3. UTF-8 フラグ (General Purpose Bit Flag bit 11) がない場合、Shift-JIS でデコード
4. UTF-8 lossy デコード結果 (libarchive.js が生成する文字化け名) とのマッピングを構築
5. `extractFiles()` 結果のオブジェクトキーをパスセグメント単位で置換

非ZIP (RAR/7z) 用のフォールバックとして、Latin-1 として保存されたバイトから Shift-JIS を復元する `tryFixFilename` も備えています。

## 技術メモ

### libarchive.js WASM の CDN 読み込み

ブラウザのセキュリティ制約上、クロスオリジン URL から直接 Web Worker を生成できません。以下のワークアラウンドで解決しています:

1. jsDelivr CDN から `worker-bundle.js` を `fetch()` でテキスト取得
2. コード内の `import.meta.url` を CDN の実 URL 文字列に正規表現で置換
3. 置換済みコードから `Blob` → `blob:` URL を生成して Worker を起動
4. Worker 内の WASM ファイルパス解決が CDN 上で正しく動作

```
libarchive.js (7.9KB) ── import ──→ libarchive.js CDN module
worker-bundle.js (60KB) ── fetch → patch → blob: URL → Worker
libarchive.wasm (979KB) ── Worker が CDN から自動 fetch
```

### 遅延読み込み

libarchive.js は初回のアーカイブファイル読み込み時に動的 `import()` されます。PDF のみ使用する場合は WASM のダウンロードは発生しません。

---

# Comic Viewer (English)

A browser-based universal comic viewer. Supports PDF / CBZ / CBR / CB7 / EPUB. Self-contained in a single HTML file.

Created by id-fa, built with Claude Code.

## Supported Formats

| Format | Extension | Library |
|--------|-----------|---------|
| PDF | `.pdf` | [PDF.js](https://mozilla.github.io/pdf.js/) v4.9.155 |
| CBZ | `.cbz`, `.zip` | [libarchive.js](https://github.com/nika-begiashvili/libarchivejs) v2.0.2 (WASM) |
| CBR | `.cbr`, `.rar` | libarchive.js v2.0.2 (WASM) |
| CB7 | `.cb7`, `.7z` | libarchive.js v2.0.2 (WASM) |
| EPUB | `.epub` | libarchive.js v2.0.2 (WASM) — Fixed-layout (image-based) only |

Image files within archives (JPEG, PNG, WebP, GIF, BMP, AVIF, JXL, TIFF) are automatically detected and displayed.

> **About EPUB**: EPUB files are internally ZIP archives, so they are extracted and image files are displayed. Only fixed-layout EPUBs (where each page is composed of images) are supported. Reflowable (text-based) EPUBs are not supported. For reflowable EPUBs, try [BiBI](https://id-fa.github.io/bibi-extension-ImageExporter/DEMO/).

## Usage

### Getting Started

A local HTTP server is required because WASM Workers do not work with the `file://` protocol.

```bash
# Python
cd pdf-viewer-with-screenshot
python -m http.server 8000

# PHP
php -S localhost:8000

# Node.js (npx)
npx serve .
```

Open `http://localhost:8000/comic-viewer.html` in your browser.

### Opening Files

- Click the "Open" button to select a file
- Drag & drop a file onto the page

### Viewer Controls

| Control | Description |
|---------|-------------|
| `<` / `>` buttons | Page navigation |
| Page number input | Jump to a specific page |
| Single / Spread | Toggle single page / two-page spread |
| Right (R2L) / Left (L2R) | Binding direction (R2L = Japanese manga, L2R = Western books) |
| Cover | Treat the cover as a standalone page |
| HQ | High-quality PDF downscale mode (render at 1x → step-halve downscale) |
| 50% ~ 200% / Fit | Display scale |
| Thumbs | Show thumbnail sidebar |

### Keyboard / Tap Controls

| Input | R2L (Right-to-Left) | L2R (Left-to-Right) |
|-------|---------------------|---------------------|
| ← / Tap left edge | Next page | Previous page |
| → / Tap right edge | Previous page | Next page |
| ↑ | Previous page | Previous page |
| ↓ | Next page | Next page |
| Home | First page | First page |
| End | Last page | Last page |

Click/tap the left or right 1/3 of the screen to navigate pages. Tap the center 1/3 to toggle UI visibility.

| Input | Description |
|-------|-------------|
| Center tap / `H` key | Toggle header UI visibility |
| `Escape` | Show UI |
| Left/right swipe (touch) | Page navigation (smartphone support) |

When the UI is hidden, the "Fit" display scale uses the full viewport height for a larger view.

### Image Export

| Button | Action |
|--------|--------|
| Save Page | Save the current page (combined two pages in spread mode) |
| Save 2P | Save the current page + next page as a spread |
| Save All | Save all pages with sequential numbering |

Output format options: PNG / JPEG 95% / WebP 95%.
- PDF: Rendered at 2x scale
- Archive images: Native resolution

### Sort Order (Archives Only)

You can change the sort order of image filenames within an archive:

| Sort | Behavior | Example |
|------|----------|---------|
| Natural (default) | Compares numbers numerically | `img_1 → img_2 → img_3 → img_10 → img_100` |
| Lexical | Character code order (dictionary) | `img_1 → img_10 → img_100 → img_11 → img_2` |
| Timestamp | Sorted by file modification date | Oldest → Newest (Natural fallback for identical timestamps) |

### Nested Archives

When a CBZ contains multiple CBZ/ZIP/RAR/7z files (nested archives):

1. The outer archive is extracted and a list of inner archives is displayed
2. Select one inner archive to extract
3. Only the selected inner archive is extracted and displayed

Since not all archives are extracted at once, this works efficiently even with nested archives containing a large number of files.

### ZIP Filename Encoding Fix

ZIP/CBZ files created on Windows may have filenames encoded in Shift-JIS (CP932). libarchive.js (WASM) interprets these as UTF-8, causing garbled text.

To fix this, the ZIP central directory is parsed directly to recover correct filenames:

1. Scan the ZIP file's End of Central Directory Record (EOCD)
2. Extract filename byte sequences from each central directory entry
3. If the UTF-8 flag (General Purpose Bit Flag bit 11) is not set, decode as Shift-JIS
4. Build a mapping from UTF-8 lossy-decoded results (garbled names from libarchive.js)
5. Replace object keys from `extractFiles()` results on a per-path-segment basis

For non-ZIP formats (RAR/7z), a `tryFixFilename` fallback is provided to recover Shift-JIS from bytes stored as Latin-1.

## Technical Notes

### Loading libarchive.js WASM from CDN

Due to browser security constraints, Web Workers cannot be created directly from cross-origin URLs. The following workaround is used:

1. Fetch `worker-bundle.js` as text from the jsDelivr CDN via `fetch()`
2. Replace `import.meta.url` in the code with the actual CDN URL string using regex
3. Create a `Blob` → `blob:` URL from the patched code to launch the Worker
4. WASM file path resolution within the Worker correctly points to the CDN

```
libarchive.js (7.9KB) ── import ──→ libarchive.js CDN module
worker-bundle.js (60KB) ── fetch → patch → blob: URL → Worker
libarchive.wasm (979KB) ── Worker auto-fetches from CDN
```

### Lazy Loading

libarchive.js is dynamically `import()`ed when an archive file is first loaded. If only PDFs are used, the WASM download does not occur.
