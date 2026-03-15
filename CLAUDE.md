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
- 回転設定が適用された状態でエクスポートされる

## comic-viewer.html

### 依存
- **PDF.js** v4.9.155 — CDN (`cdnjs.cloudflare.com`)
- **libarchive.js** v2.0.2 — CDN (`cdn.jsdelivr.net`) — WASM ベース、遅延読み込み

### 対応形式
- PDF — PDF.js でレンダリング
- CBZ / ZIP — libarchive.js (WASM) で展開
- CBR / RAR — libarchive.js (WASM) で展開
- CB7 / 7z — libarchive.js (WASM) で展開
- EPUB — libarchive.js (WASM) で展開 ※固定レイアウト(画像ベース)のみ対応

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
- 回転設定が適用された状態でエクスポートされる

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

### ミニマップ (Map チェックボックス)
- **ON**: 右下に固定表示のミニマップを表示。全体の縮小画像＋赤枠で現在の表示エリアを示す
  - コンテンツが画面に収まっている場合は自動非表示 (スクロール不要時は表示しない)
  - ミニマップ上をクリック/ドラッグで表示位置をジャンプ移動
  - スクロール・リサイズ・ページ切替時に `requestAnimationFrame` でスロットリング更新
  - `updateMinimap()` — ビューア内のcanvasをミニマップcanvasに縮小描画し、ビューポート矩形を更新
  - パンモードのドラッグと干渉しないよう `minimapDragging` フラグで排他制御
- **OFF** (デフォルト): ミニマップ非表示
- 最大サイズ: 200×300px、ドキュメントの縦横比に合わせて自動スケーリング

### パンモード (Pan チェックボックス)
- **ON**: ドラッグ操作が画面パン（スクロール）になる。拡大表示時に便利
  - マウスドラッグ: `window.scrollTo()` でスクロール位置を移動、カーソルが grab/grabbing に変化
  - マウスホイール: ブラウザ標準のスクロール動作 (ページ送り無効)
  - タッチスワイプ: ブラウザ標準のスクロール動作 (ページ送り無効)
  - 画面左右タップによるページ送りは無効化 (中央タップのUIトグルのみ有効)
- **OFF** (デフォルト): ページ送り優先 (従来動作)

### 回転表示 (Rotate)
- プルダウンで 0° / 90° / 180° / 270° を選択
- `getRotation()` — 現在の回転角度を返す
- `rotateCanvas(srcCanvas)` — canvas を現在の回転角度で回転した新しい canvas を返す
- `getScale()` の Fit 計算時: 90°/270° では幅と高さを入れ替えてフィット計算
- 表示 (`renderView()`): レンダリング後に `rotateCanvas()` を適用
- エクスポート (`exportPageCanvas()`): レンダリング後に `rotateCanvas()` を適用 → 見開き結合保存にも反映

### 高品質縮小 (HQ モード)
- `drawImageHighQuality()` — `createImageBitmap` + `resizeQuality: 'high'` (Lanczos3 相当) で高品質縮小描画
- `applySharpen()` — 3x3ラプラシアンカーネルによるアンシャープマスク (amount=0.4)、HQ縮小後に適用
- **アーカイブ画像** (comic-viewer.html): 常時 `drawImageHighQuality()` 適用、HQチェックON + 縮小時にシャープネスも適用
- **PDF** (両ビューア共通): HQ チェックボックスで切替可能
  - OFF (デフォルト): PDF.js が直接ターゲットスケールでレンダリング (軽量)
  - ON: PDF.js で 1x レンダリング → `drawImageHighQuality()` で縮小 → `applySharpen()` でシャープネス適用 (高品質・重い)
  - `s < 1` (Fit, 50%, 75% 等の縮小表示) の場合のみ HQ パスを通る
  - サムネイルにも適用される

### レイアウト中央揃え
- `.viewer` は `align-items: center` を使わない (拡大時に左端が見切れる問題を回避)
- 代わりに `.spread-container` / `.page-container` に `margin-left: auto; margin-right: auto` で中央揃え
- コンテンツが画面内に収まる時は中央配置、画面より大きい時は左端(0,0)からスクロール可能

### しおり（ブックマーク）機能
- サイドバーを「Bookmarks」「Thumbs」の排他タブに分割
- localStorage にファイルハッシュ (SHA-256先頭16文字、`file.name + '|' + file.size`) とページ番号を保存
- `BOOKMARK_STORE_FILENAME` 変数 (デフォルト `false`) でファイル名の保存可否を制御（プライバシー保護）
- **手動しおり**: サムネイル上の `●` マーカークリックでトグル
- **自動しおり**: `lastRead` (最後に表示したページ) / `furthest` (到達最深ページ) を `renderView()` 時に自動更新
- Bookmarksタブ: しおり付きページをサムネイル表示（canvas クローン）、ヘッダーにページ番号・種別表示
- Thumbsタブ: 従来サムネイル + しおりマーカー、自動しおりはページ番号ラベルのオレンジ背景で表現
- 管理機能 (Bookmarksタブ下部): Clear this book / Clear all / Export JSON / Import JSON
- comic-viewer.html の二重アーカイブ時は外側+内側ファイル名を結合してハッシュ生成
- サイドバーの `top` は ResizeObserver でヘッダー高さに追従

### アノテーションコメント表示 (PDF)
- PDF読み込み時に全ページの `page.getAnnotations()` を走査し、`contents` を持つアノテーションを収集
- コメントが1件以上ある場合、左下にフローティングボタン (💬 + 件数バッジ) を表示
- クリックでモーダル表示: ページ別グループ、タイプ・著者・日時・コメント内容
- セキュリティ: `textContent` 経由でエスケープし HTML/JS は動作しない
- comic-viewer.html ではアーカイブ読み込み時にFABを非表示にリセット

### 関数
- `getSpreadPages(pageNum)` — スプレッド構成を返す ([left, right] or [single])
- `canonicalPage(pageNum)` — ページ番号をペアの先頭に正規化
- `prevPageNum()` / `nextPageNum()` — ナビゲーション計算

## 開発規約
- Vanilla JS のみ、フレームワーク不使用
- 単一HTMLファイルを維持 (外部ファイル分割しない)
- ES Modules (`type="module"`) で記述
- Chrome DevTools MCP で動作確認可能
