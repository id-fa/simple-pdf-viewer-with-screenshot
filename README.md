# PDF Viewer with Screenshot

ブラウザベースのビューア＋画像エクスポートツール群。PWA としてインストール可能、オフラインで動作する。

A browser-based viewer + image export toolkit. Installable as a PWA and fully functional offline.

Created by id-fa, built with Claude Code.

## ファイル構成 / File Structure

```
pdf-viewer-with-screenshot/
├── pdf-viewer.html        # PDF専用ビューア / PDF-only viewer
├── comic-viewer.html      # 汎用ビューア / Universal viewer (PDF + CBZ/CBR/CB7/EPUB)
├── sw.js                  # Service Worker (precache + COOP/COEP)
├── manifest.webmanifest   # PWA manifest
├── vendor/                # Vendored libraries (no CDN required)
│   ├── pdfjs/             #   PDF.js v4.9.155
│   ├── pica/              #   Pica.js v10.0.2
│   ├── libarchive/        #   libarchive.js v2.0.2
│   └── vips/              #   wasm-vips v0.0.18 (optional, used with ?vips=1)
├── icons/                 # PWA icons + generator
├── README.md              # This file
└── CLAUDE.md              # AI development guide
```

---

## pdf-viewer.html

PDF専用の軽量ビューア。

A lightweight PDF-only viewer.

### 使い方 / Usage

ローカルにダウンロードせず、GitHub Pages でホストされたページをそのまま利用できます。アクセス解析 (Google Analytics) はありますが、開いたファイルの内容は一切サーバーに送信されません。

You can use the viewer directly via GitHub Pages without downloading. Google Analytics is used for access analytics, but the contents of files you open are never sent to any server.

→ [**Open pdf-viewer**](https://id-fa.github.io/simple-pdf-viewer-with-screenshot/webapp/pdf-viewer.html)
→ [**Open pdf-viewer (wasm-vips)**](https://id-fa.github.io/simple-pdf-viewer-with-screenshot/webapp/pdf-viewer.html?vips=1) ※wasm-vips 版 (初回アクセス時に vendor 一式 ~8MB をキャッシュ) / wasm-vips build (~8MB of vendored assets cached on first visit)

ローカルに置いて使う場合は、`vendor/` の ES モジュールが `file://` では CORS でブロックされるため HTTP サーバーが必要です (comic-viewer.html と同じ、下記「起動 / Getting Started」を参照)。

To use it locally, an HTTP server is required — the ES modules under `vendor/` are blocked by CORS on `file://` (same as comic-viewer.html; see "Getting Started" below).

1. `http://localhost:8000/pdf-viewer.html` をブラウザで開く / Open `http://localhost:8000/pdf-viewer.html` in your browser
2. 「Open PDF」ボタンまたはドラッグ＆ドロップでPDFを読み込む / Load a PDF via "Open PDF" button or drag & drop
3. ページを閲覧・画像として保存 / Browse pages and save as images

### 依存 / Dependencies

- [PDF.js](https://mozilla.github.io/pdf.js/) v4.9.155 (`vendor/` に同梱 / vendored)
- [Pica.js](https://github.com/nodeca/pica) v10.0.2 (`vendor/` に同梱 / vendored) — 高品質画像縮小 (unsharp mask、Web Worker で実行) / High-quality image downscaling, runs in a Web Worker

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
| EPUB | `.epub` | libarchive.js v2.0.2 (WASM) ※固定レイアウト + リフロー本文表示 / Fixed-layout + reflowable text view |

アーカイブ内の画像ファイル (JPEG, PNG, WebP, GIF, BMP, AVIF, JXL, TIFF) を自動検出して表示します。

Image files within archives are automatically detected and displayed.

> **EPUB について / About EPUB**: ページ画像としての表示は固定レイアウト(画像ベース)のみ。リフロー型は `R` キーの本文リーダで中身のテキストを読めます (縦書き・ページ分割は非対応)。本格的なリフロー表示には [BiBI](https://id-fa.github.io/bibi-extension-ImageExporter/DEMO/) をお試しください。 / Only fixed-layout (image-based) EPUBs render as pages. Reflowable EPUBs can be read as text via the `R` key reader (no vertical writing or pagination). For a full reflowable experience, try [BiBI](https://id-fa.github.io/bibi-extension-ImageExporter/DEMO/).

### 起動 / Getting Started

ローカルにサーバーを立てる代わりに、GitHub Pages でホストされたページをそのまま利用できます。アクセス解析 (Google Analytics) はありますが、開いたファイルの内容は一切サーバーに送信されません。

Instead of running a local server, you can use the viewer directly via GitHub Pages. Google Analytics is used for access analytics, but the contents of files you open are never sent to any server.

→ [**Open comic-viewer**](https://id-fa.github.io/simple-pdf-viewer-with-screenshot/webapp/comic-viewer.html)
→ [**Open comic-viewer (wasm-vips)**](https://id-fa.github.io/simple-pdf-viewer-with-screenshot/webapp/comic-viewer.html?vips=1) ※wasm-vips 版 (初回アクセス時に vendor 一式 ~8MB をキャッシュ) / wasm-vips build (~8MB of vendored assets cached on first visit)

ローカルで起動する場合は、`file://` では WASM Worker が動作しないため HTTP サーバーが必要です。

To run locally, a local HTTP server is required because WASM Workers do not work with `file://`.

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
| HQ | 高品質縮小モード (Pica.js) / High-quality downscale mode (Pica.js) |
| 0° / 90° / 180° / 270° | ページ回転 / Page rotation |
| 50% ~ 300% / Fit | 表示スケール / Display scale |
| Pan | ドラッグで画面パン / Drag to pan (scroll) |
| Map | ミニマップ表示 / Show minimap |
| Full | フルスクリーン / Fullscreen mode |
| Filter | 色調補正フィルター (プリセット3スロット保存可) / Color filters (3 preset slots) |
| Thumbs / Bookmarks / TOC | サイドバー切替 (TOC は EPUB 構造解析後のみ) / Sidebar tabs (TOC appears only after EPUB analysis) |

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
| `C` キー | Cover (表紙モード) トグル / Toggle cover mode |
| `B` キー | 綴じ方向切替 (R2L ↔ L2R) / Toggle binding direction |
| `Z` キー | ズームトグル (300% + Pan + Map) / Toggle zoom (300% + Pan + Map) |
| `L` キー | Last Read ページにジャンプ / Jump to last read page |
| `M` キー | Max Read ページにジャンプ / Jump to max read page |
| `E` キー | EPUB 構造解析 (ページ順の修正) / Analyze EPUB structure (fix page order) |
| `T` キー | EPUB 目次の開閉 / Toggle EPUB table of contents |
| `R` キー | EPUB 本文テキストを表示 / Open the EPUB text reader |
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
- **自動しおり**: 最後に開いたページ (last read) と到達最深ページ (max read) を自動記録 / Auto-records last read and max read page
- **しおり一覧**: Bookmarksタブにサムネイル付きで表示、クリックでジャンプ / Displayed with thumbnails in Bookmarks tab
- **管理**: 現在の本のしおり消去、全消去、JSON export/import / Clear per book, clear all, JSON export/import
- **データ共有**: 両ビューアで同じ localStorage キーを使用 / Both viewers share the same localStorage keys

### EPUB 構造解析・目次 / EPUB Structure & TOC

EPUB はファイル名順が読み順と一致しないことがあります。`E` キーで EPUB 内部の構造 (`container.xml` → `content.opf` の spine) を解析し、正しいページ順に並べ替えます。処理が重いため自動実行はせず、キー操作でのみ実行します。
EPUB filenames often don't match reading order. Press `E` to analyze the EPUB structure (`container.xml` → OPF spine) and reorder pages correctly. This is opt-in because the analysis is expensive.

- spine の XHTML を辿って `<img>` を文書順に収集。spine から画像が取れない場合は manifest の `image/*` の記述順にフォールバック / Walks the spine's XHTML for `<img>` in document order; falls back to manifest `image/*` order
- 解析後は並び順プルダウンに `Sort: EPUB` が追加され、他の並び順にいつでも戻せる / Adds a `Sort: EPUB` option so you can switch back anytime
- 目次が定義されていればサイドバーに `TOC` タブが出現 (`T` キーで開閉)。EPUB3 nav / EPUB2 NCX の両方に対応、階層表示・ページ番号付き・クリックでジャンプ / A `TOC` tab appears when a table of contents exists (`T` to toggle); supports both EPUB3 nav and EPUB2 NCX

### リフロー型 EPUB の本文表示 / Reflowable EPUB Text Reader

構造解析 (`E`) 後、`TOC` タブ下部の「本文を読む」または `R` キーで、EPUB 内の XHTML をそのまま読めるリーダが開きます。画像を 1 枚も持たないリフロー型 EPUB も開けます。

After structure analysis (`E`), press `R` (or use "本文を読む" at the bottom of the `TOC` tab) to open a reader that shows the EPUB's own XHTML. Image-less reflowable EPUBs can be opened too.

- 文書間の移動 (‹ / › / ← / → / プルダウン)、文字サイズ変更 (A- / A+)、表示幅の切替 (幅: 標準 ⇄ 広い、次回以降も保持)、「原文CSS」の ON/OFF / Document navigation, font size, reader width (normal / wide, remembered), and an "original CSS" toggle
- **外部リソースは完全にブロック**: `allow-scripts` なしの sandbox iframe + `default-src 'none'` の CSP + http(s) URL の除去の 3 重防御。画像・CSS はアーカイブ内から blob: URL として差し替え / **No external resources**: sandboxed iframe without `allow-scripts`, a `default-src 'none'` CSP, and stripping of http(s) URLs. In-archive images/CSS are swapped to blob: URLs
- 縦書き指定は横書きに矯正されます (本ビューアは縦書き要件を対象外としています) / Vertical writing is forced to horizontal

### 連続スクロールモード / Scroll Mode

viewMode を **Scroll** に切り替えると、全ページを縦に並べて連続スクロール表示します (Webtoon形式)。

Switch viewMode to **Scroll** to display all pages in a continuous vertical scroll (Webtoon-style).

- Fit スケール時は幅フィット / Width-fit in Fit scale
- Home / End キーで先頭・末尾にジャンプ / Home/End to jump to first/last page
- Save 2P は縦連結 (上下) で保存 / Save 2P saves vertically concatenated

### 色調補正フィルター / Color Adjustment Filters

**Filter** ボタンでポップアップを開き、4種のスライダーで色調を調整できます。

Click **Filter** to open the popup and adjust colors with 4 sliders.

| スライダー / Slider | 範囲 / Range | 説明 / Description |
|------|------|------|
| Brightness | 50% – 150% | 明るさ (CSS filter) |
| Contrast | 50% – 150% | コントラスト (CSS filter) |
| Sepia | 0% – 100% | セピア (CSS filter) |
| Invert | 0% – 100% | 色反転 (CSS filter) |
| Sharpen | 0 – 500 | シャープネス強度 (Pica unsharp mask) |
| Sh.Radius | 0.5 – 2.0 | シャープネスぼかし半径 / Unsharp blur radius |
| Sh.Thresh | 0 – 255 | シャープネスしきい値 / Unsharp threshold |

- **Sharpen 操作時**: HQ 未チェックなら自動的に HQ を ON にする / Auto-enables HQ when Sharpen is adjusted
- **プリセット保存**: 3スロット (Save 1-3 / Load 1-3)。localStorage に保存され、両ビューアで共有。シャープネス設定も含む / 3 preset slots shared between both viewers via localStorage, including sharpness settings
- **Reset**: 全スライダーを初期値に戻す / Reset all sliders to default

### wasm-vips (オプション) / wasm-vips (Optional)

`?vips=1` を URL に付加すると、Pica.js の代わりに [wasm-vips](https://www.npmjs.com/package/wasm-vips) (libvips WASM) による高品質画像縮小が有効になります。

Append `?vips=1` to the URL to enable [wasm-vips](https://www.npmjs.com/package/wasm-vips) (libvips WASM) for high-quality image downscaling instead of Pica.js.

- 例 / Example: `comic-viewer.html?vips=1`, `pdf-viewer.html?vips=1`
- HTTP サーバーが必要 (`file://` では動作しない) / Requires HTTP server (does not work with `file://`)
- 設定は localStorage (`vipsEnabled`) に保存され、Filter ポップアップ末尾のトグルでも切替可能。`?vips=1` は書き込み用のワンショット / The setting is stored in localStorage (`vipsEnabled`) and can also be toggled at the bottom of the Filter popup; `?vips=1` is just a one-shot way to write it
- WASM モジュール (~4.8MB) + JS ローダー (~78KB) は、オフライン動作のため `?vips=1` の有無にかかわらず Service Worker がプリキャッシュする。`?vips=1` が無効な間はロード・実行されないだけ / The WASM module (~4.8MB) + JS loader (~78KB) are precached by the Service Worker regardless of `?vips=1` so that offline mode works; without `?vips=1` they are simply never loaded or executed
- vips ロード失敗時、および画像処理中のメモリ不足時は自動的に Pica にフォールバック / Falls back to Pica automatically on load failure, and per-call on out-of-memory during processing

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

- [PDF.js](https://mozilla.github.io/pdf.js/) v4.9.155 — PDF rendering (vendored)
- [Pica.js](https://github.com/nodeca/pica) v10.0.2 — High-quality image downscaling with unsharp mask, runs in a Web Worker (vendored)
- [wasm-vips](https://www.npmjs.com/package/wasm-vips) v0.0.18 (libvips 8.18.3) — Optional high-quality resize via libvips WASM (`?vips=1`, vendored)
- `sw.js` (self-hosted Service Worker) — Precache for offline use + COOP/COEP headers for SharedArrayBuffer (wasm-vips only)
- [libarchive.js](https://github.com/nika-begiashvili/libarchivejs) v2.0.2 — Archive extraction (vendored, WASM, comic-viewer.html only)
- Vanilla JavaScript (ES Modules)
- ビューア本体は HTML 1枚 (JS/CSS を外部ファイルに分割しない)、フレームワーク不使用。ライブラリのみ `vendor/` に配置 / Each viewer is a single HTML file (no split-out JS/CSS) with no frameworks; only third-party libraries live under `vendor/`

### ベンダー化 / Vendored Libraries

外部ライブラリはすべて `vendor/` に同梱されており、CDN への接続は発生しません。PWA としてインストールした後は、インターネット接続なしで全機能が利用できます。

All third-party libraries are bundled under `vendor/`; no CDN requests are made. Once installed as a PWA, every feature works fully offline.

| ファイル / File | サイズ / Size | 用途 / Purpose |
|------|------|------|
| `vendor/pdfjs/pdf.min.mjs` + `pdf.worker.min.mjs` | ~1.6MB | PDF レンダリング / PDF rendering |
| `vendor/pdfjs/cmaps/*.bcmap` | ~1.5MB (169 files) | フォント未埋め込み CJK PDF 用 CMap / CMaps for CJK PDFs without embedded fonts |
| `vendor/pdfjs/standard_fonts/` | ~800KB (16 files) | 標準14フォント未埋め込み PDF の代替フォント / Substitute fonts for the standard 14 |
| `vendor/pica/pica.js` | 53KB | 高品質縮小 (自己完結型 ESM、Worker をバンドル済み) / High-quality downscaling (self-contained ESM with bundled worker) |
| `vendor/libarchive/` | ~1MB (JS 68KB + WASM 979KB) | アーカイブ展開 / Archive extraction |
| `vendor/vips/` | ~4.9MB (JS 78KB + WASM 4.8MB) | wasm-vips (`?vips=1` 時のみ使用) / wasm-vips (only used with `?vips=1`) |

libarchive.js の Worker は同一オリジンから直接起動します (CDN 時代に必要だった `fetch` → `import.meta.url` 差し替え → `blob:` URL のワークアラウンドは不要になりました)。

The libarchive.js worker is launched directly from the same origin — the old CDN workaround (fetch as text → patch `import.meta.url` → run from a `blob:` URL) is no longer needed.

```js
const workerUrl = new URL('./vendor/libarchive/worker-bundle.js', location.href).href;
Archive.init({ getWorker: () => new Worker(workerUrl, { type: 'module' }) });
```

### 読み込みタイミング / Load Timing

ページ実行時のモジュールロードは遅延します。libarchive.js は**初回のアーカイブ読み込み時**、wasm-vips は **`?vips=1` が有効なとき**にのみ動的 `import()` されます。PDF だけを開く場合、これらは評価されません。

Module loading at runtime is lazy: libarchive.js is dynamically `import()`ed **on the first archive load**, and wasm-vips **only when `?vips=1` is enabled**. Neither is evaluated if you only open PDFs.

ただし Service Worker は登録時 (初回アクセス時) に `vendor/` 配下の主要ファイルを**一括プリキャッシュ**します。これにはオフライン動作のため `libarchive.wasm` と `vips.wasm` も含まれるため、初回アクセス時のネットワーク転送量は用途にかかわらず約 8MB になります。CMap と standard_fonts はプリキャッシュ対象外で、PDF が要求したときにのみ取得・キャッシュされます。

Note that the Service Worker **precaches** the main `vendor/` files when it installs (on first visit). For offline support this includes `libarchive.wasm` and `vips.wasm`, so the first visit transfers roughly 8MB regardless of what you use it for. CMaps and standard fonts are excluded from the precache and are fetched (and cached) on demand only when a PDF requires them.
