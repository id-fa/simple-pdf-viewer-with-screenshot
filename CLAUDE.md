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
- クリップボードコピー対応 (詳細は共通セクション参照)

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
- `archiveImages[]` — ソート済み画像配列 (アーカイブ時) `{name, blob, img, width, height, lastModified, animated}`
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

### 対応画像形式
- `IMAGE_EXTS`: JPEG, PNG, WebP, GIF, BMP, AVIF, JXL, TIFF, HEIC, HEIF
- HEIC/HEIF は Safari のみ対応 (Chrome/Firefox 非対応)
- ブラウザ非対応の形式は `loadImageEntries` で `loadImageFromBlob` 失敗時にスキップ

### 画像エクスポート
- PDF: 2x スケール (pdf-viewer.html と同じ)
- アーカイブ画像: ネイティブ解像度 (1x) でエクスポート
- ファイル名: `{ファイル名}_{ページ番号}.{ext}` (ゼロパディング)
- 見開き表示時: `Save Page` ボタンが `Save p{左ページ番号}` / `Save p{右ページ番号}` の2つに置き換わる
- 回転設定が適用された状態でエクスポートされる
- クリップボードコピー対応 (詳細は共通セクション参照)

### アニメーション画像再生 (comic-viewer.html)
- `isAnimatedImage(blob)` — GIF (画像ブロック 0x2C が2つ以上) / WebP (RIFF内 "ANIM" チャンク) / APNG (PNG内 "acTL" チャンク) を判定
- `loadImageEntries` で各画像に `animated` フラグを付与
- アニメーション画像ページには左下に "▶ Play" バッジを表示 (`addGifBadge()`)
- バッジクリックでモーダルが開き、blob URL の `<img>` でアニメーション再生
- canvas 表示は静止画 (1フレーム目)、モーダルでのみアニメーション再生
- モーダルは背景クリックまたは Escape キーで閉じる

### アーカイブ展開のセキュリティ対策 (comic-viewer.html)
- **パストラバーサル防止**: `sanitizePath()` で `..` / `.` セグメントと先頭スラッシュを除去
- **ファイル数制限**: `ARCHIVE_MAX_FILES` (10,000) 超過で展開中断・エラー表示
- **展開サイズ制限**: `ARCHIVE_MAX_TOTAL_SIZE` (2 GB) 超過で展開中断・エラー表示 (Zip Bomb 対策)
- WASM サンドボックスにより libarchive 本体のバッファオーバーフロー等の CVE は RCE に繋がらない

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
- C キー: Cover (表紙モード) トグル
- B キー: 綴じ方向 (R2L ↔ L2R) トグル
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

### パスワード保護ファイル対応
- **PDF** (両ビューア共通): PDF.js の `onPassword` コールバックでパスワード入力ダイアログを表示
  - `showPasswordDialogPDF(fileName, errorMsg)` — パスワード入力ダイアログ (Promise ベース)
  - 間違ったパスワード入力時: PDF.js が `PasswordResponses.INCORRECT_PASSWORD` で再コールバック → エラーメッセージ付きで再表示
  - キャンセル時: 空文字列を `updatePassword()` に渡してエラーを発生させ、呼び出し側の try-catch でトースト表示
- **アーカイブ** (comic-viewer.html): libarchive.js の `hasEncryptedData()` / `usePassword()` で対応
  - `showPasswordDialog(archiveName)` — アーカイブ用パスワード入力ダイアログ
  - `extractArchiveWithPassword(file, fnMap, loadingEl)` — 暗号化検出 → パスワード入力 → 展開の一連フロー
  - ヘッダーで暗号化を検出できないケース (ZIP個別エントリ暗号化等): `extractFiles()` のエラーメッセージで検出しリトライ
  - 二重アーカイブの内部アーカイブもパスワード付きに対応
  - ※暗号化ファイル名の7zは libarchive の制限で非対応の可能性あり

### クリップボードコピー (両ビューア共通)
- `formatSelect` に **Clipboard (View)** と **Clipboard (Page)** の2つのオプションを追加
- **Clipboard (Page)**: ページ全体を 2x スケール (PDF) またはネイティブ解像度 (アーカイブ) で PNG としてクリップボードにコピー
  - `copyCanvasToClipboard(canvas)` — `canvas.toBlob()` → `navigator.clipboard.write()` + `ClipboardItem`
  - Save All ボタンを無効化 (一括コピーは無意味)
- **Clipboard (View)**: 現在ビューポートに表示されているエリアのみをキャプチャしてクリップボードにコピー
  - `captureVisibleArea()` — ビューア内の全 canvas のビューポート可視矩形を計算し、1枚の canvas に合成
  - Save All と Save 2P ボタンを無効化
- `isClipboardMode()` / `isClipboardView()` — formatSelect の値から判定
- `updateFormatButtons()` — formatSelect の change イベントで Save All / Save 2P の disabled を制御
- `saveCanvas()` 内でクリップボードモードを判定し、ファイル保存の代わりにクリップボードコピーを実行

### フルスクリーンモード (Full チェックボックス)
- **ON**: `document.documentElement.requestFullscreen()` でブラウザフルスクリーン化
- **OFF**: `document.exitFullscreen()` で解除
- ブラウザ側の操作 (Escキー等) でフルスクリーンが解除された場合、`fullscreenchange` イベントでチェック状態を同期
- Fit スケール時はフルスクリーン切替後に `renderView()` を再実行してサイズ調整
- WebKit プレフィックス (`webkitRequestFullscreen` / `webkitExitFullscreen`) にも対応
- 両ビューア (pdf-viewer.html, comic-viewer.html) に実装

### テキストモード (Text チェックボックス、pdf-viewer.html のみ)
- **ON**: PDF テキストの選択・コピー・検索が可能なモードに切替
  - PDF.js `TextLayer` API でキャンバス上に透明テキストスパンをオーバーレイ
  - `--scale-factor` CSS変数を明示的に設定し、テキストレイヤーのサイズをキャンバスに一致させる
  - テキスト選択: `color: transparent` + `::selection` でブラウザネイティブの選択・コピー動作
  - 回転対応: 0°/90°/180°/270° に応じてテキストレイヤーに CSS `transform: rotate()` + 位置オフセットを適用
  - クリック・タッチスワイプ・ホイールによるページ送りを無効化 (テキスト選択優先)
  - キーボード矢印キーでのページ送りは維持
- **OFF** (デフォルト): テキストレイヤー非表示、通常のページ送り動作
- **検索ツールバー**: Text ON時にヘッダー下部に表示
  - テキスト入力欄 (300ms デバウンス) + マッチ数表示 (`N / M`) + ▲/▼ ナビゲーションボタン
  - 全ページの `page.getTextContent()` を走査してマッチを収集 (結果は `pageTextCache` にキャッシュ)
  - マッチしたスパンを黄色 (`rgba(255,220,0,0.35)`) でハイライト、現在のマッチはオレンジ (`rgba(255,120,0,0.55)`)
  - Enter / Shift+Enter で次/前のマッチへ移動 (ページ跨ぎ対応、自動ジャンプ)
  - Escape で Text モードを解除
  - `position: sticky` で表示、`updateSidebarTop()` でヘッダー高さに追従
- 状態変数: `textMode`, `pageTextCache` (ページ番号→テキストデータ), `searchMatches[]`, `currentMatchIdx`
- PDF再読み込み時にキャッシュをクリア

### アノテーションコメント表示 (PDF)
- PDF読み込み時に全ページの `page.getAnnotations()` を走査し、`contents` を持つアノテーションを収集
- コメントが1件以上ある場合、左下にフローティングボタン (💬 + 件数バッジ) を表示
- クリックでモーダル表示: ページ別グループ、タイプ・著者・日時・コメント内容
- セキュリティ: `textContent` 経由でエスケープし HTML/JS は動作しない
- comic-viewer.html ではアーカイブ読み込み時にFABを非表示にリセット

### 連続スクロールモード (Scroll、両ビューア共通)
- viewMode セレクトに **Scroll** オプションを追加
- 全ページを縦に並べて連続スクロール表示 (Webtoon形式)
- `isScrollMode()` — スクロールモード判定
- `renderScrollView(jumpTo)` — 全ページのプレースホルダを生成し、IntersectionObserver で遅延レンダリング
- `renderScrollPage(pageNum, container)` — 個別ページのcanvasをレンダリング
- `updateScrollCurrentPage()` — ビューポート中央に最も近いページを currentPage として追跡
- Fit スケール時は幅フィットのみ (高さ制約なし、縦スクロール前提)
- ページ送り操作 (wheel, click zones, swipe) は無効化 → ブラウザ標準スクロール
- Home/End キーで先頭/末尾ページにジャンプ
- サムネイルクリック・ページ番号入力でのジャンプに対応
- `<` / `>` ボタンは disabled
- **Save 2P**: 通常モードでは横に結合するが、Scrollモードでは縦に連結して保存 (p1が上、p2が下、幅が異なる場合は中央揃え)

### 色調補正フィルター (Filter、両ビューア共通)
- ヘッダーに **Filter** ボタン + ポップアップ
- スライダー4種: Brightness (50-150%), Contrast (50-150%), Sepia (0-100%), Invert (0-100%)
- `applyFilters()` — CSS `filter` プロパティを `.viewer` に適用
- Reset ボタンで初期値に復帰
- ポップアップ外クリックで自動クローズ
- **プリセット保存**: 3スロット (Save 1-3 / Load 1-3)、localStorage キー `viewerFilterPresets` でシステム共通 (ファイル毎ではない)
  - Save ボタンで現在のスライダー値を保存、Load ボタンで復元・即時適用
  - 未保存スロットの Load ボタンは disabled、保存済みスロットはツールチップに設定値を表示

### ヘルプモーダル (?、両ビューア共通)
- `?` キーまたはヘッダーの **?** ボタンでモーダル表示
- キーボードショートカット・マウス/タッチ操作の一覧を表示
- Escape または背景クリックで閉じる
- pdf-viewer.html はテキストモード操作の説明も含む

### 関数
- `getSpreadPages(pageNum)` — スプレッド構成を返す ([left, right] or [single])
- `canonicalPage(pageNum)` — ページ番号をペアの先頭に正規化
- `prevPageNum()` / `nextPageNum()` — ナビゲーション計算

## 開発規約
- Vanilla JS のみ、フレームワーク不使用
- 単一HTMLファイルを維持 (外部ファイル分割しない)
- ES Modules (`type="module"`) で記述
- Chrome DevTools MCP で動作確認可能
