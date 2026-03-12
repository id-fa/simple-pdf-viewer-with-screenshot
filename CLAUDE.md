# PDF Viewer with Screenshot

## プロジェクト概要
ブラウザベースのビューア＋画像エクスポートツール群。単一HTMLファイルで完結する設計。

## ファイル構成
- `pdf-viewer.html` — PDF専用ビューア (PDF.jsのみ依存)
- `comic-viewer.html` — 汎用コミックビューア (PDF + CBZ/CBR/CB7対応)

## 共通アーキテクチャ
- **単一ファイル構成**: 各HTMLファイルにHTML/CSS/JSを全て内包
- **CDN依存**: 外部ライブラリはCDNから読み込み (ローカルファイル不要)
- **ES Modules**: `<script type="module">` で記述
- **Vanilla JS**: フレームワーク不使用

## pdf-viewer.html

### 依存
- **PDF.js** v4.9.155 — CDN (`cdnjs.cloudflare.com`) から ES Module

### 状態管理
- `pdfDoc` — PDF.js のドキュメントオブジェクト
- `currentPage` — 現在表示中のページ番号 (1-based、見開き時はペアの小さい方)
- `totalPages` — 総ページ数
- `rendering` — レンダリング中フラグ (二重実行防止)

### 画像エクスポート
- エクスポート用: 固定 2x スケール (`exportPageCanvas()`)
- ファイル名: `{PDFファイル名}_{ページ番号}.{ext}` (ゼロパディング)
- 見開き表示時: `Save Page` ボタンが `Save p{左ページ番号}` / `Save p{右ページ番号}` の2つに置き換わる

## comic-viewer.html

### 依存
- **PDF.js** v4.9.155 — CDN (`cdnjs.cloudflare.com`)
- **libarchive.js** v2.0.2 — CDN (`cdn.jsdelivr.net`) — WASM ベース、遅延読み込み

### 対応形式
- PDF — PDF.js でレンダリング
- CBZ / ZIP — libarchive.js (WASM) で展開
- CBR / RAR — libarchive.js (WASM) で展開
- CB7 / 7z — libarchive.js (WASM) で展開

### WASM Worker のクロスオリジン対策
ブラウザはクロスオリジン URL から直接 Worker を生成できないため:
1. `worker-bundle.js` を CDN から `fetch()` でテキスト取得
2. コード内の `import.meta.url` を CDN の実 URL 文字列に置換
3. 置換済みコードから `Blob` → `blob:` URL を生成
4. `Archive.init({ getWorker })` でカスタム Worker ファクトリを登録
→ Worker 内から WASM ファイルのパスが正しく CDN に解決される

### 状態管理
- `docType` — `'pdf'` | `'archive'`
- `pdfDoc` — PDF.js ドキュメント (PDF時)
- `archiveImages[]` — ソート済み画像配列 (アーカイブ時) `{name, blob, img, width, height, lastModified}`
- `archiveImagesUnsorted[]` — 展開順の画像配列 (ソート切替用の元データ)
- `currentPage` / `totalPages` / `rendering` — pdf-viewer.html と同じ

### ソート機能 (アーカイブ時のみ表示)
- **Natural** — 数字部分を数値比較 (`img_1 → img_2 → img_10 → img_100`)
- **Lexical** — 文字コード順 (`img_1 → img_10 → img_100 → img_2`)
- **Timestamp** — `File.lastModified` 順、同一時刻なら Natural フォールバック

### 二重アーカイブ対応
外側アーカイブに内部アーカイブ (`.cbz/.zip/.cbr/.rar/.cb7/.7z`) が含まれる場合:
1. 外側を展開してファイル一覧を取得 (File オブジェクト生成のみ、画像ロードはしない)
2. 内部アーカイブを検出 → 選択ダイアログ表示 (ファイル名 + サイズ)
3. ユーザーが選んだ内部アーカイブのみを展開 → 画像をロード・表示
4. ファイル名表示: `外側.cbz > 内側.cbz (Np)`

### 画像エクスポート
- PDF: 2x スケール (pdf-viewer.html と同じ)
- アーカイブ画像: ネイティブ解像度 (1x) でエクスポート
- ファイル名: `{ファイル名}_{ページ番号}.{ext}` (ゼロパディング)
- 見開き表示時: `Save Page` ボタンが `Save p{左ページ番号}` / `Save p{右ページ番号}` の2つに置き換わる

### 実行要件
- ローカル HTTP サーバー必須 (`python -m http.server`, `php -S localhost:8000` 等)
- `file://` では WASM Worker が動作しない
- インターネット接続必須 (CDN から PDF.js + libarchive.js を読み込み)

## 共通: 見開き表示ロジック

### ページペアリング
- **Cover ON**: ページ1単独 → 2-3, 4-5, 6-7, ...
- **Cover OFF**: 1-2, 3-4, 5-6, ...
- 最終ページが余る場合は単独表示

### 綴じ方向と配置
- **R2L (右綴じ)**: `[大きいページ番号 | 小さいページ番号]` — 日本の漫画レイアウト
- **L2R (左綴じ)**: `[小さいページ番号 | 大きいページ番号]` — 洋書レイアウト

### R2L時のナビゲーション反転
- `<` / `>` ボタン: R2L時は動作が反転 (`<` = 次ページ、`>` = 前ページ)
- ボタンの disabled 状態もR2Lに対応 (最終ページで `<` 無効、最初のページで `>` 無効)

### キーボード / タッチ操作
- R2L時: ←キー = 次ページ、→キー = 前ページ (読み方向に合致)
- L2R時: ←キー = 前ページ、→キー = 次ページ (通常方向)
- Home / End: 最初 / 最後のページ
- H キー: ヘッダーUI表示/非表示トグル
- Escape: UI再表示
- 画面左右1/3タップ: ページ送り、中央1/3タップ: UI表示/非表示トグル
- 左右スワイプ (タッチ): ページ送り (スマートフォン対応)

### UI非表示モード
- ヘッダーを `max-height: 0` で畳む方式 (DOM上の高さが0になり隙間が出ない)
- `body.ui-hidden` クラスでサイドバー・プログレスバー・ビューアの位置も連動
- Fit スケール時はヘッダー分の高さも使って拡大表示 (`getScale()` が `isUIHidden()` を参照)
- `toggleUI(forceShow?)` — トグル関数、トランジション完了後に `renderView()` を再実行

### 高品質縮小 (HQ モード)
- `drawImageHighQuality()` — `createImageBitmap` + `resizeQuality: 'high'` (Lanczos3 相当) で高品質縮小描画
- `applySharpen()` — 3x3ラプラシアンカーネルによるアンシャープマスク (amount=0.4)、HQ縮小後に適用
- **アーカイブ画像** (comic-viewer.html): 常時 `drawImageHighQuality()` 適用、HQチェックON + 縮小時にシャープネスも適用
- **PDF** (両ビューア共通): HQ チェックボックスで切替可能
  - OFF (デフォルト): PDF.js が直接ターゲットスケールでレンダリング (軽量)
  - ON: PDF.js で 1x レンダリング → `drawImageHighQuality()` で縮小 → `applySharpen()` でシャープネス適用 (高品質・重い)
  - `s < 1` (Fit, 50%, 75% 等の縮小表示) の場合のみ HQ パスを通る
  - サムネイルにも適用される

### 関数
- `getSpreadPages(pageNum)` — スプレッド構成を返す ([left, right] or [single])
- `canonicalPage(pageNum)` — ページ番号をペアの先頭に正規化
- `prevPageNum()` / `nextPageNum()` — ナビゲーション計算

## 開発規約
- Vanilla JS のみ、フレームワーク不使用
- 単一HTMLファイルを維持 (外部ファイル分割しない)
- ES Modules (`type="module"`) で記述
- Chrome DevTools MCP で動作確認可能
