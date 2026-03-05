# PDF Viewer with Screenshot

ブラウザベースのPDFビューア＋画像エクスポートツール。ローカルのPDFファイルを開いて、ページを画像として保存できる。

A browser-based PDF viewer and image export tool. Open local PDF files and save pages as images.

[open viewer](https://id-fa.github.io/simple-pdf-viewer-with-screenshot/webapp/)

## 使い方 / Usage

`pdf-viewer.html` をブラウザで直接開く。サーバー不要。

Open `pdf-viewer.html` directly in your browser. No server required.

1. 「Open PDF」ボタンまたはドラッグ＆ドロップでPDFを読み込む / Open a PDF via the "Open PDF" button or drag & drop
2. ページを閲覧・ナビゲーション / Browse and navigate pages
3. 必要なページを画像として保存 / Save pages as images

## 機能 / Features

### ビューア / Viewer
- **単ページ / 見開き表示** — Single / Spread view toggle
- **綴じ方向** — 右綴じ (R2L) / 左綴じ (L2R) binding direction toggle
- **表紙モード** — Cover ON で1ページ目を単独表示、以降見開きペアリング / Cover mode: page 1 displayed alone, then paired spreads
- **ズーム** — 50% / 75% / 100% / 150% / 200% / Fit (ウィンドウフィット / fit to window)
- **サムネイル** — 左サイドバーにページ一覧、クリックでジャンプ / Thumbnail sidebar with click-to-jump
- **キーボード操作** — 矢印キーでページ送り (R2L時は左右反転)、Home/End / Arrow keys for navigation (reversed in R2L mode), Home/End

### 画像保存 / Image Export
- **Save Page** — 現在表示中のページを画像保存 (見開き時は2ページ結合) / Save current page (merged spread in spread view)
- **Save 2P** — 現在ページ＋次ページの見開き画像を保存 / Save current + next page as a spread image
- **Save All** — 全ページを一括保存 (プログレスバー付き) / Save all pages with progress bar
- **出力形式 / Format** — PNG / JPEG 95% / WebP 95%
- **解像度 / Resolution** — 2x スケールで高品質出力 / 2x scale for high-quality output

## 技術スタック / Tech Stack

- [PDF.js](https://mozilla.github.io/pdf.js/) 4.9.155 — PDF rendering (CDN)
- Vanilla JavaScript (ES Modules)
- 単一HTMLファイル、依存なし / Single HTML file, no dependencies

## ファイル構成 / File Structure

```
pdf-viewer-with-screenshot/
├── pdf-viewer.html   # Main application
├── README.md         # This file
└── CLAUDE.md         # AI development guide
```
