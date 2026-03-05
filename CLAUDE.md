# PDF Viewer with Screenshot

## プロジェクト概要
ブラウザベースのPDFビューア＋画像エクスポートツール。単一HTMLファイルで完結。

## アーキテクチャ
- **単一ファイル構成**: `pdf-viewer.html` にHTML/CSS/JSを全て内包
- **PDF.js**: CDN (`cdnjs.cloudflare.com`) から ES Module として読み込み (v4.9.155)
- **レンダリング**: PDF.js → Canvas → 表示 / Blob → ダウンロード

## 主要な状態管理
- `pdfDoc` — PDF.js のドキュメントオブジェクト
- `currentPage` — 現在表示中のページ番号 (1-based、見開き時はペアの小さい方)
- `totalPages` — 総ページ数
- `rendering` — レンダリング中フラグ (二重実行防止)

## 見開き表示ロジック

### ページペアリング
- **Cover ON**: ページ1単独 → 2-3, 4-5, 6-7, ... (奇数ページが左/右の先頭)
- **Cover OFF**: 1-2, 3-4, 5-6, ... (ページ1から即ペアリング)
- 最終ページが余る場合は単独表示

### 綴じ方向と配置
- **R2L (右綴じ)**: `[大きいページ番号 | 小さいページ番号]` — 日本の漫画レイアウト
- **L2R (左綴じ)**: `[小さいページ番号 | 大きいページ番号]` — 洋書レイアウト

### キーボードナビゲーション
- R2L時: ←キー = 次ページ、→キー = 前ページ (読み方向に合致)
- L2R時: ←キー = 前ページ、→キー = 次ページ (通常方向)

### 関数
- `getSpreadPages(pageNum)` — 指定ページのスプレッド構成を返す ([left, right] or [single])
- `canonicalPage(pageNum)` — ページ番号をペアの先頭に正規化
- `navStep(pageNum)` / `prevPage()` / `nextPage()` — ナビゲーション計算

## 画像エクスポート
- 表示用: `getScale()` で計算したスケール (Fit対応)
- エクスポート用: 固定 2x スケール (`exportPageCanvas()`)
- 見開き保存: 2枚のCanvasを横結合、綴じ方向に応じて配置順を変更
- ファイル名: `{PDFファイル名}_{ページ番号}.{ext}` (ゼロパディング)

## 開発規約
- Vanilla JS のみ、フレームワーク不使用
- 単一HTMLファイルを維持 (外部ファイル分割しない)
- CDN依存は PDF.js のみ
- ES Modules (`type="module"`) で記述
