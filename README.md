# PDF Viewer with Screenshot

ブラウザベースのビューア＋画像エクスポートツール群。単一HTMLファイルで完結する設計。

A browser-based viewer + image export toolkit. Each viewer is self-contained in a single HTML file.

Created by id-fa, built with Claude Code.

[open pdf-viewer](https://id-fa.github.io/simple-pdf-viewer-with-screenshot/webapp/pdf-viewer.html)

[open comic-viewer](https://id-fa.github.io/simple-pdf-viewer-with-screenshot/webapp/comic-viewer.html)

## ファイル構成 / File Structure

```
pdf-viewer-with-screenshot/
├── pdf-viewer.html    # PDF専用ビューア / PDF-only viewer
├── comic-viewer.html  # 汎用ビューア / Universal viewer (PDF + CBZ/CBR/CB7/EPUB)
├── README.md          # This file
└── CLAUDE.md          # AI development guide
```

---

## pdf-viewer.html

PDF専用の軽量ビューア。サーバー不要、`file://` で動作する。

A lightweight PDF-only viewer. No server required, works with `file://`.

### 使い方 / Usage

1. `pdf-viewer.html` をブラウザで直接開く / Open `pdf-viewer.html` directly in your browser
2. 「Open PDF」ボタンまたはドラッグ＆ドロップでPDFを読み込む / Load a PDF via "Open PDF" button or drag & drop
3. ページを閲覧・画像として保存 / Browse pages and save as images

### 依存 / Dependencies

- [PDF.js](https://mozilla.github.io/pdf.js/) v4.9.155 (CDN)

---

## comic-viewer.html

PDF / CBZ / CBR / CB7 / EPUB に対応する汎用コミックビューア。

A universal comic viewer supporting PDF / CBZ / CBR / CB7 / EPUB.

### 対応形式 / Supported Formats

| 形式 / Format | 拡張子 / Extension | ライブラリ / Library |
|------|--------|-----------|
| PDF | `.pdf` | [PDF.js](https://mozilla.github.io/pdf.js/) v4.9.155 |
| CBZ | `.cbz`, `.zip` | [libarchive.js](https://github.com/nika-begiashvili/libarchivejs) v2.0.2 (WASM) |
| CBR | `.cbr`, `.rar` | libarchive.js v2.0.2 (WASM) |
| CB7 | `.cb7`, `.7z` | libarchive.js v2.0.2 (WASM) |
| EPUB | `.epub` | libarchive.js v2.0.2 (WASM) ※固定レイアウトのみ / Fixed-layout only |

アーカイブ内の画像ファイル (JPEG, PNG, WebP, GIF, BMP, AVIF, JXL, TIFF) を自動検出して表示します。

Image files within archives are automatically detected and displayed.

> **EPUB について / About EPUB**: 固定レイアウト(画像ベース)のみ対応。リフロー型EPUBには [BiBI](https://id-fa.github.io/bibi-extension-ImageExporter/DEMO/) をお試しください。 / Only fixed-layout (image-based) EPUBs are supported. For reflowable EPUBs, try [BiBI](https://id-fa.github.io/bibi-extension-ImageExporter/DEMO/).

### 起動 / Getting Started

`file://` では WASM Worker が動作しないため、ローカル HTTP サーバーが必要です。

A local HTTP server is required because WASM Workers do not work with `file://`.

```bash
# Python
python -m http.server 8000

# PHP
php -S localhost:8000

# Node.js (npx)
npx serve .
```

ブラウザで `http://localhost:8000/comic-viewer.html` を開きます。

Open `http://localhost:8000/comic-viewer.html` in your browser.

---

## 共通機能 / Common Features

以下の機能は両ビューアに共通です。 / The following features are shared by both viewers.

### ビューア操作 / Viewer Controls

| 操作 / Control | 説明 / Description |
|------|------|
| `<` / `>` ボタン | ページ送り / Page navigation |
| ページ番号入力 | 任意ページにジャンプ / Jump to a specific page |
| Single / Spread / Scroll | 単ページ / 見開き / 連続スクロール切替 / Toggle single / spread / scroll |
| Right (R2L) / Left (L2R) | 綴じ方向 / Binding direction (R2L=日本漫画, L2R=洋書) |
| Cover | 表紙を単独ページとして扱う / Treat cover as standalone page |
| HQ | PDF縮小時の高品質モード / High-quality PDF downscale mode |
| 0° / 90° / 180° / 270° | ページ回転 / Page rotation |
| 50% ~ 300% / Fit | 表示スケール / Display scale |
| Pan | ドラッグで画面パン / Drag to pan (scroll) |
| Map | ミニマップ表示 / Show minimap |
| Full | フルスクリーン / Fullscreen mode |
| Filter | 色調補正フィルター (プリセット3スロット保存可) / Color filters (3 preset slots) |
| Thumbs / Bookmarks | サイドバー切替 / Sidebar tabs |

### キーボード・タッチ操作 / Keyboard & Touch

| 操作 / Input | R2L (右綴じ) | L2R (左綴じ) |
|------|-------------|-------------|
| ← / 画面左端タップ | 次ページ / Next | 前ページ / Prev |
| → / 画面右端タップ | 前ページ / Prev | 次ページ / Next |
| ↑ | 前ページ / Prev | 前ページ / Prev |
| ↓ | 次ページ / Next | 次ページ / Next |
| Home | 最初のページ / First page | 最初のページ / First page |
| End | 最後のページ / Last page | 最後のページ / Last page |

| 操作 / Input | 説明 / Description |
|------|------|
| 画面中央タップ / `H` キー | UI表示/非表示トグル / Toggle UI visibility |
| `Escape` | UI再表示 / Show UI |
| 左右スワイプ | ページ送り (スマホ対応) / Page navigation (touch) |

### 画像エクスポート / Image Export

| ボタン / Button | 動作 / Action |
|--------|------|
| Save Page | 現在のページを保存 (見開き時は2ページ結合) / Save current page (merged in spread) |
| Save 2P | 現在+次ページの見開きを保存 (スクロールモードでは縦連結) / Save current + next as spread (vertical in scroll mode) |
| Save All | 全ページを連番で保存 / Save all pages sequentially |

- **出力形式 / Format**: PNG / JPEG 95% / WebP 95%
- **解像度 / Resolution**: PDF は 2x スケール、アーカイブ画像はネイティブ解像度 / PDF at 2x scale, archive images at native resolution
- **回転 / Rotation**: 回転設定が適用された状態でエクスポートされる (見開き結合保存を含む) / Rotation setting is applied to exports (including spread merge saves)

### しおり (ブックマーク) / Bookmarks

- **手動しおり**: サムネイル上の `●` マーカーをクリックして設定/解除 / Click `●` marker on thumbnail to set/unset
- **自動しおり**: 最後に開いたページ (last read) と到達最深ページ (furthest) を自動記録 / Auto-records last read and furthest page
- **しおり一覧**: Bookmarksタブにサムネイル付きで表示、クリックでジャンプ / Displayed with thumbnails in Bookmarks tab
- **管理**: 現在の本のしおり消去、全消去、JSON export/import / Clear per book, clear all, JSON export/import
- **データ共有**: 両ビューアで同じ localStorage キーを使用 / Both viewers share the same localStorage keys

### 連続スクロールモード / Scroll Mode

viewMode を **Scroll** に切り替えると、全ページを縦に並べて連続スクロール表示します (Webtoon形式)。

Switch viewMode to **Scroll** to display all pages in a continuous vertical scroll (Webtoon-style).

- Fit スケール時は幅フィット / Width-fit in Fit scale
- Home / End キーで先頭・末尾にジャンプ / Home/End to jump to first/last page
- Save 2P は縦連結 (上下) で保存 / Save 2P saves vertically concatenated

### 色調補正フィルター / Color Adjustment Filters

**Filter** ボタンでポップアップを開き、4種のスライダーで色調を調整できます。

Click **Filter** to open the popup and adjust colors with 4 sliders.

| スライダー / Slider | 範囲 / Range |
|------|------|
| Brightness | 50% – 150% |
| Contrast | 50% – 150% |
| Sepia | 0% – 100% |
| Invert | 0% – 100% |

- **プリセット保存**: 3スロット (Save 1-3 / Load 1-3)。localStorage に保存され、両ビューアで共有 / 3 preset slots shared between both viewers via localStorage
- **Reset**: 全スライダーを初期値に戻す / Reset all sliders to default

### アノテーションコメント (PDF) / Annotation Comments

PDFにアノテーションコメントがある場合、左下にフローティングボタン (💬) が表示されます。クリックでモーダル表示。

When a PDF contains annotation comments, a floating button (💬) appears. Click to view in a modal grouped by page.

---

## comic-viewer.html 固有機能 / comic-viewer.html Specific Features

### ソート順 (アーカイブ時のみ) / Sort Order (Archives Only)

| ソート / Sort | 動作 / Behavior | 例 / Example |
|--------|------|-----|
| Natural (デフォルト) | 数字を数値比較 / Numeric comparison | `img_1 → img_2 → img_10 → img_100` |
| Lexical | 文字コード順 / Dictionary order | `img_1 → img_10 → img_100 → img_2` |
| Timestamp | 更新日時順 / By modification date | 古い→新しい / Oldest → Newest |

### 二重アーカイブ / Nested Archives

CBZ 内に複数のアーカイブが含まれている場合:

When a CBZ contains multiple archive files:

1. 外側を展開し内部アーカイブの一覧を表示 / Extract outer, show list of inner archives
2. 展開したい内部アーカイブを1つ選択 / Select one inner archive
3. 選択分のみを展開・表示 / Extract and display only the selected one

### アニメーション画像 / Animated Images

アーカイブ内の GIF / APNG / Animated WebP を自動検出します。該当ページの左下に **▶ Play** バッジが表示され、クリックするとモーダルでアニメーション再生できます。

Animated GIF / APNG / Animated WebP files in archives are auto-detected. A **▶ Play** badge appears on the page; click it to play the animation in a modal.

- canvas 上の表示は静止画 (1フレーム目) です。アニメーション再生はモーダル内のみ / Canvas shows only the first frame; animation plays only in the modal
- 複数のアニメーション画像の連続再生には対応していません / Continuous playback of multiple animated images is not supported
- 動画ファイル (MP4, WebM 等) の再生には対応していません / Video files (MP4, WebM, etc.) are not supported

### ZIPファイル名エンコーディング修正 / ZIP Filename Encoding Fix

Windows で作成された ZIP/CBZ の Shift-JIS ファイル名の文字化けを自動修正。ZIP の中央ディレクトリを直接パースして正しいファイル名を復元します。

Automatically fixes garbled Shift-JIS filenames in Windows-created ZIP/CBZ files by parsing the ZIP central directory.

---

## 技術メモ / Technical Notes

### 技術スタック / Tech Stack

- [PDF.js](https://mozilla.github.io/pdf.js/) v4.9.155 — PDF rendering (CDN)
- [libarchive.js](https://github.com/nika-begiashvili/libarchivejs) v2.0.2 — Archive extraction (CDN, WASM, comic-viewer.html only)
- Vanilla JavaScript (ES Modules)
- 単一HTMLファイル、フレームワーク不使用 / Single HTML files, no frameworks

### libarchive.js WASM の CDN 読み込み / Loading WASM from CDN

クロスオリジン制約のワークアラウンド: / Cross-origin workaround:

1. `worker-bundle.js` を CDN から `fetch()` でテキスト取得 / Fetch as text from CDN
2. `import.meta.url` を CDN URL に置換 / Replace with actual CDN URL
3. `Blob` → `blob:` URL で Worker 起動 / Launch Worker via blob: URL

```
libarchive.js (7.9KB) ── import ──→ CDN module
worker-bundle.js (60KB) ── fetch → patch → blob: URL → Worker
libarchive.wasm (979KB) ── Worker が CDN から自動 fetch / auto-fetched by Worker
```

### 遅延読み込み / Lazy Loading

libarchive.js は初回のアーカイブファイル読み込み時に動的 `import()` されます。PDF のみ使用する場合は WASM のダウンロードは発生しません。

libarchive.js is dynamically `import()`ed on first archive load. WASM download does not occur if only PDFs are used.
