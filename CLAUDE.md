# PDF Viewer with Screenshot

## プロジェクト概要
ブラウザベースのビューア＋画像エクスポートツール群。PWA としてインストール可能、オフライン動作対応。

## ファイル構成
- `pdf-viewer.html` — PDF専用ビューア
- `comic-viewer.html` — 汎用コミックビューア (PDF + CBZ/CBR/CB7/EPUB対応)
- `sw.js` — Service Worker (プリキャッシュ + COOP/COEP ヘッダー付与)
- `manifest.webmanifest` — PWA マニフェスト
- `vendor/` — ベンダー化された外部ライブラリ (CDN不要)
  - `pdfjs/pdf.min.mjs` `pdf.worker.min.mjs` — PDF.js v4.9.155
  - `pica/pica.js` — Pica.js v9.0.1
  - `libarchive/libarchive.js` `worker-bundle.js` `libarchive.wasm` — libarchive.js v2.0.2
  - `vips/vips-es6.js` `vips.wasm` — wasm-vips (`?vips=1` 時のみロード)
- `icons/` — PWA アイコン (192 / 512 / maskable) + 生成スクリプト `_generate.py`
- `Firefly_Gemini_icon_776910.png` — アイコン右下に合成する意匠素材

## 共通アーキテクチャ
- **PWA**: Service Worker によるプリキャッシュでオフライン動作、ホーム画面にインストール可
- **ローカル資産のみ**: CDN 依存なし (全ライブラリを `vendor/` に同梱)
- **ES Modules**: `<script type="module">` で記述
- **Vanilla JS**: フレームワーク不使用
- **wasm-vips オプション**: `?vips=1` クエリパラメータ / Filter ポップアップのトグル / manifest shortcut で切替可能 (両ビューア共通)

## pdf-viewer.html

### 依存
- **PDF.js** v4.9.155 — `vendor/pdfjs/` からローカル読み込み
- **Pica.js** v9.0.1 — `vendor/pica/pica.js` — 高品質画像縮小 (Lanczos3 + unsharp mask)

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
- **PDF.js** v4.9.155 — `vendor/pdfjs/` からローカル読み込み
- **Pica.js** v9.0.1 — `vendor/pica/pica.js`
- **libarchive.js** v2.0.2 — `vendor/libarchive/` — WASM ベース、遅延読み込み

### 対応形式
- PDF — PDF.js でレンダリング
- CBZ / ZIP — libarchive.js (WASM) で展開
- CBR / RAR — libarchive.js (WASM) で展開
- CB7 / 7z — libarchive.js (WASM) で展開
- EPUB — libarchive.js (WASM) で展開 ※固定レイアウト(画像ベース)のみ対応

### libarchive Worker
同一オリジンなので `new Worker(workerUrl, { type: 'module' })` で直接生成。
`LIBARCHIVE_BASE = './vendor/libarchive/'` + `location.href` でワーカーURLを絶対URLに解決し、
Worker 内部の `new URL('libarchive.wasm', import.meta.url)` が正しく WASM ファイルに到達する。

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
- `file://` では WASM Worker / Service Worker が動作しない
- インターネット接続は**不要** (全ライブラリを `vendor/` にベンダー化済み、PWA初回インストール後はオフラインで全機能利用可)

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
- Z キー: ズームトグル (300% + Pan + Map ↔ 元の設定に復元)
- L キー: Last Read ページにジャンプ (しおり未有効時はエラーダイアログ)
- M キー: Max Read ページにジャンプ (しおり未有効時はエラーダイアログ)
- Escape: UI再表示 (UI表示中に2秒以内にもう一度押すと `location.reload()` でファイルを閉じてドロップ画面に戻る。1回目押下時は「もう一度 ESC で閉じる」トーストを2秒表示。モーダル (ヘルプ / GIFオーバーレイ / パスワード入力) が開いているときはモーダル閉じが優先)
- 画面左右1/3タップ: ページ送り、中央1/3タップ: UI表示/非表示トグル
- 左右スワイプ (タッチ): ページ送り (スマートフォン対応)

### ヘッダーレイアウト
- `.header` は `flex-wrap: wrap` で、`.title-row` と `.controls` の2要素
- `.title-row` にアプリ名 (`<h1>`) とファイル名 (`#fileInfo`) を横並び配置
- ウィンドウ幅が狭い場合は `.controls` が次行に折り返し (最大2行)
- ヘッダー高さの変化は ResizeObserver で監視し、Fit スケール時に `renderView()` を再実行
- `window.resize` イベントでも Fit スケール時に再描画

### UI非表示モード
- ヘッダーを `max-height: 0` で畳む方式 (DOM上の高さが0になり隙間が出ない)
- `body.ui-hidden` クラスでサイドバー・プログレスバー・ビューアの位置も連動
- Fit スケール時はヘッダー分の高さも使って拡大表示 (`getScale()` が `isUIHidden()` を参照、`header.offsetHeight` で動的取得)
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
- **Pica.js** ベース (デフォルト): Lanczos3 フィルタ + 組み込み unsharp mask で高品質縮小
- **wasm-vips** (オプション): `?vips=1` で有効化。thumbnailImage (box shrink + Lanczos3) + vips sharpen
- `drawImageHighQuality(ctx, img, targetW, targetH, sharpenOpts, useVips)` — vips が利用可能かつ `useVips=true` なら `drawImageVips()` にディスパッチ、失敗時 (メモリ不足等) は自動的に Pica にフォールバック
- `drawImageVips()` — `newFromMemory` → `thumbnailImage` → `sharpen` → `writeToMemory`。alpha チャンネル分離・sRGB reinterpret で colorspace エラーを回避。`toDelete` 配列で vips Image オブジェクトのメモリ管理
- Pica 初期化: `new Pica({ features: ['js', 'wasm'] })` — Web Worker は CDN ESM 環境で動作しないため無効化
- **サムネイル生成**: `renderPageToCanvas(pageNum, scale, false)` で vips をスキップし Pica を使用 (WASM ヒープ節約)
- **アーカイブ画像** (comic-viewer.html): 常時 Pica/vips 経由で縮小、Filter の Sharpen 値が適用される
- **PDF** (両ビューア共通): HQ チェックボックスで切替可能
  - OFF (デフォルト): PDF.js が直接ターゲットスケールでレンダリング (軽量)
  - ON: PDF.js で 1x レンダリング → Pica/vips で縮小 + Sharpen 適用 (高品質・重い)
  - `s < 1` (Fit, 50%, 75% 等の縮小表示) の場合のみ HQ パスを通る
  - サムネイルにも適用される
  - HQ チェック時に Sharpen が 0 なら自動的にデフォルト値 (80) を設定

### wasm-vips オプション (localStorage `vipsEnabled`、両ビューア共通)
- **有効化方法 (3通り)**:
  1. URL クエリ: `?vips=1` を付加 (例: `comic-viewer.html?vips=1`) — 初回アクセス時の設定スイッチ
  2. アプリ内トグル: Filter ポップアップ末尾「HQ engine: wasm-vips」チェック
  3. Manifest shortcut: PWAインストール後、ランチャー長押し → 「Comic HQ」「PDF HQ」
- **設定のソースは localStorage**: `VIPS_ENABLED = localStorage.getItem('vipsEnabled') === '1'`。`?vips=1` は単に localStorage に書き込むためのワンショット。URL の `?vips=1` 付け替えリダイレクトは行わない (cold start launchQueue を保護するため)。トグルOFFで localStorage から削除 + reload
- **依存ファイル**: `vendor/vips/vips-es6.js` (87KB) + `vendor/vips/vips.wasm` (5.4MB)
- **COOP/COEP 付与**: `sw.js` が全レスポンスに `Cross-Origin-Embedder-Policy: require-corp` / `Cross-Origin-Opener-Policy: same-origin` / `Cross-Origin-Resource-Policy: cross-origin` を付与 (SharedArrayBuffer 有効化)。初回ロード時は SW が controller になるまで `controllerchange` を待ってリロード
- **初期化**: `dynamicLibraries: []` で不要な JXL/HEIF/RESVG モジュールのロードをスキップ。`vips.Cache.max(0)` でオペレーションキャッシュを無効化 (WASM ヒープ節約)
- **フォールバック**: vips ロード失敗時は自動的に Pica にフォールバック。画像処理中のメモリ不足エラーも per-call で Pica にフォールバック
- **WASM ヒープ制約**: WASM メモリ空間に上限があるため、高解像度画像で `newFromMemory` がメモリ不足になる場合がある。サムネイル生成では vips をスキップしてヒープを温存
- **ステータス表示**: `?vips=1` 時のみ dropzone に「wasm-vips active」または「vips failed → Pica fallback」を表示
- **`?vips=1` なしの場合**: vips の import は発生しない (COOP/COEP は付与されるが動作に影響なし)

### レイアウト中央揃え
- `.viewer` は `align-items: center` を使わない (拡大時に左端が見切れる問題を回避)
- 代わりに `.spread-container` / `.page-container` に `margin-left: auto; margin-right: auto` で中央揃え
- コンテンツが画面内に収まる時は中央配置、画面より大きい時は左端(0,0)からスクロール可能

### しおり（ブックマーク）機能
- サイドバーを「Bookmarks」「Thumbs」の排他タブに分割
- localStorage にファイルハッシュ (SHA-256先頭16文字、`file.name + '|' + file.size`) とページ番号を保存
- `BOOKMARK_STORE_FILENAME` 変数 (デフォルト `false`) でファイル名の保存可否を制御（プライバシー保護）
- **手動しおり**: サムネイル上の `●` マーカークリックでトグル
- **自動しおり**: `lastRead` (最後に表示したページ) / `maxRead` (到達最深ページ) を `renderView()` 時に自動更新
- **セッション Last Read**: ファイル読み込み時に前回の `lastRead` を `sessionLastRead` に保持。L キーはこの値にジャンプ (リアルタイム更新される `bm.lastRead` ではなく前回セッションの値)
- ファイル読み込み完了時、`sessionLastRead` が2以上なら `showClickableToast()` で「p.X から再開」トーストを5秒表示。クリックで該当ページにジャンプ
- `updateAutoBookmarks` は `sessionLastRead` が存在し `pageNum === 1` の場合にスキップ (初回 `renderView(1)` で前回の `lastRead` を上書きしない。ユーザーがページ移動するまで保持)
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
- PDF読み込み時に全ページの `page.getAnnotations()` を走査し、コメントを持つアノテーションを収集 (`Popup` サブタイプは重複するため除外)
- PDF.js v4 ではプロパティ名が変更されている: `contents` → `contentsObj.str`、`title` → `titleObj.str` (旧プロパティにもフォールバック)
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
- **CSS フィルター** (即時適用): Brightness (50-150%), Contrast (50-150%), Sepia (0-100%), Invert (0-100%)
- **シャープネス** (Pica unsharp mask、再レンダリング必要):
  - Sharpen (0-500): unsharpAmount、シャープネス強度。0 = 無効
  - Sh.Radius (0.5-2.0): unsharpRadius、ぼかし半径。内部は整数 5-20 で管理し /10 で表示
  - Sh.Thresh (0-255): unsharpThreshold、適用しきい値。差がこの値以下のピクセルは無視
  - Sharpen 操作時に HQ 未チェックなら自動的に HQ を ON にする (PDF では HQ パスでのみ Pica が使われるため)
  - 変更時は 300ms debounce で `rerenderForSharpen()` を実行
  - `getSharpenOpts()` — スライダー値から Pica の unsharp オプションオブジェクトを返す
- `applyFilters()` — CSS `filter` プロパティを `.viewer` に適用 + シャープネス値の表示更新
- Reset ボタンで全スライダーを初期値に復帰 (シャープネスは 0, Radius=0.6, Threshold=2)
- ポップアップ外クリックで自動クローズ
- **プリセット保存**: 3スロット (Save 1-3 / Load 1-3)、localStorage キー `viewerFilterPresets` でシステム共通 (ファイル毎ではない)
  - 保存データ: `{ b, c, s, i, sh, shr, sht }` (旧プリセットとの後方互換: `sh/shr/sht` 未設定時はデフォルト値にフォールバック)
  - Save ボタンで現在のスライダー値を保存、Load ボタンで復元・即時適用
  - 未保存スロットの Load ボタンは disabled、保存済みスロットはツールチップに設定値を表示

### UI 設定の永続化 (localStorage、両ビューア共通)
- `viewerViewMode` — Single / Spread / Scroll の選択状態 (起動時に復元、変更時に保存)
- `viewerHQ` — HQ チェックボックスの状態 (`'1'` で ON、未設定で OFF)
- `vipsEnabled` — wasm-vips 有効化フラグ (HQ engine トグル)
- `viewerFilterPresets` — Filter プリセット 3 スロット
- ブックマーク系キー (ファイルハッシュ → bookmark オブジェクト)

### ヘルプモーダル (?、両ビューア共通)
- `?` キーまたはヘッダーの **?** ボタンでモーダル表示
- キーボードショートカット・マウス/タッチ操作の一覧を表示
- Escape または背景クリックで閉じる
- pdf-viewer.html はテキストモード操作の説明も含む

### 関数
- `getSpreadPages(pageNum)` — スプレッド構成を返す ([left, right] or [single])
- `canonicalPage(pageNum)` — ページ番号をペアの先頭に正規化
- `prevPageNum()` / `nextPageNum()` — ナビゲーション計算

## PWA / Service Worker

### `sw.js`
- **`CACHE_NAME`**: バージョン文字列 (現在 `pdf-viewer-v8`)。**アセット更新時は必ず番号をインクリメント**してユーザーに新キャッシュを配信する
- **`SHARE_CACHE`**: `share-stash-v1` — Web Share Target で受信したファイルを一時保存する専用キャッシュ (activate 時も削除対象外)
- **`PRECACHE_URLS`**: インストール時に一括取得するリソース (HTML 2種、vendor/ 配下全ファイル、manifest、icons)。`fetch(url, { cache: 'reload' })` でブラウザキャッシュをバイパス
- **`activate`**: `CACHE_NAME` と `SHARE_CACHE` 以外の旧キャッシュを削除し `self.clients.claim()`
- **fetch 戦略**: 同一オリジン GET に対してのみ cache-first。キャッシュヒット時も `withCoiHeaders()` で COOP/COEP/CORP ヘッダーを付与してから返す。キャッシュミスはネットワーク取得＋成功時は自動キャッシュ
- **`handleShareTarget(request)`**: `POST` + `comic-viewer.html` 宛リクエストを傍受。`formData.getAll('file')` したファイルを `SHARE_CACHE` に `/__share__/{timestamp}-{i}` キーで保存、meta.json にファイル名/MIME/サイズを記録、`./comic-viewer.html?share=1` へ 303 リダイレクト
- **オフライン時**: キャッシュされていない同一オリジンリソースは 503 "Offline" を返す
- **外部オリジン**: `respondWith` しないのでブラウザのデフォルト挙動 (PWAでは通常発生しない)

### `manifest.webmanifest`
- **`start_url`**: `./comic-viewer.html` (ホーム画面アイコンから起動する画面)
- **`display`**: `standalone`、**`theme_color`**/**`background_color`**: `#1e293b` (slate-800)
- **`icons`**: 192/512 (`any` purpose) + 512 (`maskable`)
- **`shortcuts`** (長押しメニュー): Comic / Comic HQ (`?vips=1`) / PDF / PDF HQ (`?vips=1`)
- **`launch_handler`**: `{ client_mode: "focus-existing" }` — 既存ウィンドウを再利用してファイルを開く
- **`file_handlers`** (OS ファイル関連付け):
  - `./pdf-viewer.html` → `application/pdf` + `.pdf`
  - `./comic-viewer.html` → `.cbz/.cbr/.cb7/.epub/.zip/.rar/.7z` とそれぞれの MIME タイプ
- **`share_target`** (OS 共有メニュー受信): `action=./comic-viewer.html`、`method=POST`、`enctype=multipart/form-data`、`files` パラメータ (name=file) で PDF/アーカイブ系の MIME + 拡張子を accept

### OS ファイル関連付け / 共有ターゲット (インストール済み PWA)
- **File Handling API**: 両 HTML で `window.launchQueue.setConsumer()` を登録。OS から関連付けで起動されると `params.files[0].getFile()` で File を取得し、`openPdfFile()` / `openFile()` に渡す
- **Cold start の launchQueue 保護**: `<head>` 冒頭で launchQueue に一次コンシューマを登録し、受け取ったファイルを `window.__launchFiles` に退避。`controllerchange` リロードは launch ドキュメントを破棄してファイル情報を失うため、`__launchFiles` が存在するときは**スキップ**する (その起動は vips 無効のまま処理、次回から COI 確立)。本体スクリプトロード後に `window.__handleLaunch` を `openPdfFile`/`openFile` に差し替えて hot launch に対応
- **Web Share Target**: Android 等で共有メニューから送られた POST を SW が傍受 (`handleShareTarget`) → `share-stash-v1` キャッシュに保存 → `?share=1` 付きでリダイレクト
- **comic-viewer.html の `?share=1` ハンドラ**: ページ起動時に `caches.open('share-stash-v1')` を開き、meta.json を読み、先頭エントリの Blob を File に復元して `openFile()` に渡す。処理後は該当エントリと meta.json を削除、`history.replaceState` で URL から `?share=1` を除去
- **重要**: share_target も file_handlers も `comic-viewer.html` を action にしているので POST/GET 双方を 1 つの URL で処理する (SW が method で分岐)

### HTML側の登録
- `<head>` 冒頭のスクリプト:
  1. `?vips=1` があれば `localStorage.setItem('vipsEnabled', '1')` (ワンショットの設定書き込み)
  2. `navigator.serviceWorker.register('./sw.js')` で SW 登録
  3. `localStorage.vipsEnabled === '1'` かつ `!crossOriginIsolated` の場合のみ、`controllerchange` を待って `location.reload()` (初回のみ、COOP/COEP を反映)
- **URL リダイレクトは行わない**: 設定は localStorage に永続化されるだけ、URL は変更しない (cold start の launchQueue を保護するため)
- **ファイル読み込みエントリポイント**: `pdf-viewer.html` は `openPdfFile(file)`、`comic-viewer.html` は `openFile(file)` (PDF / アーカイブ自動判別)。file input / drag&drop / launchQueue / share_target すべてこれらを経由

### アイコン生成
- `icons/_generate.py` (Pillow) で 3サイズを再生成 (192 / 512 / maskable-512)
- ドキュメント型 + 右下に `Firefly_Gemini_icon_776910.png` を白キーで透過合成
- maskable 版は safe zone (円形クロップ) を考慮して内側に配置

## docs/webapp/
GitHub Pages 配信用の同期コピー。ルートと同じ構成 (HTML / sw.js / manifest / vendor / icons) を持つ。
ルートに変更を加えたら docs/webapp/ にも同期が必要 (HTMLは一部 diff あり: Google Analytics の gtag が入っている)。

## 開発規約
- Vanilla JS のみ、フレームワーク不使用
- HTML ファイルは単一ファイルを維持 (外部 JS/CSS に分割しない)
- ライブラリは `vendor/` に配置 (CDN に依存しない、オフラインで動作)
- ES Modules (`type="module"`) で記述
- Chrome DevTools MCP で動作確認可能
- アセット更新時は `sw.js` の `CACHE_NAME` をインクリメント
