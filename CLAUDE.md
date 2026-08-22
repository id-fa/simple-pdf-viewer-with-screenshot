# PDF Viewer with Screenshot

## プロジェクト概要
ブラウザベースのビューア＋画像エクスポートツール群。PWA としてインストール可能、オフライン動作対応。

## ファイル構成
- `pdf-viewer.html` — PDF専用ビューア
- `comic-viewer.html` — 汎用コミックビューア (PDF + CBZ/CBR/CB7/EPUB対応)
- `sw.js` — Service Worker (プリキャッシュ + COOP/COEP ヘッダー付与)
- `manifest.webmanifest` — PWA マニフェスト
- `library.php` — ライブラリ参照 API (サーバー設置時のみ。単一ファイル / 詳細は「ライブラリ参照機能」)
- `library.config.example.php` — `library.php` の設定サンプル (実体の `library.config.php` は .gitignore 済み)
- `library.htaccess.example` — Basic 認証のサンプル (Apache / nginx)
- `tools/generate_coverimages.php` — ライブラリの表紙画像 (サイドカー) を一括生成する CLI 専用スクリプト (詳細は「表紙生成ツール」)
- `tools/generate_coverimages.py` — 同じものの Python 版 (Windows ではこちらの方が導入が楽。詳細は「Python 版」)
- `vendor/` — ベンダー化された外部ライブラリ (CDN不要)
  - `pdfjs/pdf.min.mjs` `pdf.worker.min.mjs` — PDF.js v4.9.155
  - `pdfjs/cmaps/*.bcmap` — CJK (日中韓) 用の CMap データ (169 ファイル、~1.5MB)。フォント未埋め込み CJK PDF の文字解決に使用。on-demand ロード (PRECACHE 非対象 / 初回ネット使用時に `sw.js` の fetch ハンドラが自動キャッシュ)
  - `pdfjs/standard_fonts/` — Foxit/Liberation 製の代替フォント (16 ファイル、~800KB)。標準14フォント (Helvetica/Times 等) 未埋め込み PDF の代替に使用。on-demand ロード
  - `pica/pica.js` — Pica.js v10.0.3
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
- **Pica.js** v10.0.3 — `vendor/pica/pica.js` — 高品質画像縮小 (Lanczos3 + unsharp mask)

### getDocument オプション (両ビューア共通)
- `cMapUrl` — フォント未埋め込み CJK PDF (日本語/中国語/韓国語) の文字描画に必要。指定が無いと iPhone Safari 等で文字が表示されない (グリフ不一致)
- `cMapPacked: true` — `.bcmap` (バイナリ圧縮版) を使う宣言
- `standardFontDataUrl` — 標準14フォント (Helvetica/Times-Roman/Courier 等) 未埋め込み PDF の代替フォント (Foxit 製) ロード
- **重要**: `cMapUrl` / `standardFontDataUrl` は **Worker 内で fetch される**ため、相対パスはワーカー (`/vendor/pdfjs/pdf.worker.min.mjs`) を起点に解決されてしまう。`./vendor/...` を渡すと `/vendor/pdfjs/vendor/pdfjs/cmaps/...` のような二重パスになり 404。`new URL('./vendor/pdfjs/cmaps/', location.href).href` で**ページを起点とした絶対URL**に変換して渡すこと
- CMap/standard_fonts は PDF が要求した時のみ on-demand fetch される (ASCII のみの PDF では追加ダウンロード無し)

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
- **Pica.js** v10.0.3 — `vendor/pica/pica.js`
- **libarchive.js** v2.0.2 — `vendor/libarchive/` — WASM ベース、遅延読み込み

### 対応形式
- PDF — PDF.js でレンダリング
- CBZ / ZIP — libarchive.js (WASM) で展開
- CBR / RAR — libarchive.js (WASM) で展開
- CB7 / 7z — libarchive.js (WASM) で展開
- EPUB — libarchive.js (WASM) で展開 ※ページ画像として表示できるのは固定レイアウト(画像ベース)のみ。リフロー型は `R` キーの本文リーダーでテキストを読む (下記「リフロー型 EPUB の本文リーダー」)

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
- **Lexical** — 文字コード順 (`img_1 → img_10 → img_100 → img_2`)。EPUB 読み込み時の既定
- **Timestamp** — `File.lastModified` 順、同一時刻なら Natural フォールバック
- **EPUB** — `E` キーの構造解析成功時のみ選択肢に現れる (下記「EPUB 構造解析」参照)
- 並び順変更時は `rebuildAfterReorder()` を通す。Scroll モードは `.scroll-container` が残っていると古いプレースホルダが再利用されてしまうため、明示的に破棄してから再構築する

### EPUB 構造解析 (`E` キー、comic-viewer.html のみ)
EPUB はファイル名順が読み順と一致しないことが多いため、内部構造を辿って正しいページ順を組み立てる。**重い処理なので自動実行はせず `E` キーで明示起動**する (EPUB を開くと「E キーで EPUB 構造解析」トーストで案内)。

- **保持する構造ファイル**: `loadArchive` で `EPUB_STRUCT_EXTS` (`.opf/.ncx/.xhtml/.xht/.html?/.xml/.svg/.css`) にマッチしたエントリを `epubEntries` に退避 (`loadImageEntries` の第3引数)。展開時に捨てると後から解析できないため。中身はテキストなのでメモリ影響は小さい。`.css` は本文リーダーの「原文CSS」表示用 (フォントは意図的に対象外)
- **解析フロー** (`analyzeEpub()`):
  1. `META-INF/container.xml` の `rootfile[full-path]` → OPF パス (見つからなければ `*.opf` を総当たり)
  2. OPF の `manifest > item` を記述順に収集 (`id` / `href` / `media-type` / `properties`)
  3. `spine > itemref` を読み順として辿る。spine 項目が `image/*` ならそのまま採用、`application/xhtml+xml` 等ならそのファイルを読んで `<img src>` / `<svg><image xlink:href>` / インライン `style="...url(...)"` を**文書順**に収集 (`epubExtractDocImages`)
  4. spine から画像が1枚も取れなければ **manifest の `image/*` を記述順に使う**フォールバック (spine が空 / 背景画像で組まれた EPUB 等)
  5. アーカイブに実在する画像だけを採用 (重複は初出のみ)
  6. あわせて spine 上のテキスト文書 (`docs`) も読み順に収集する (本文リーダー用)。spine が空なら manifest の `application/xhtml+xml` を記述順にフォールバック
- **画像0でも成功扱い**: リフロー型 EPUB は `order` が空になる。`order` と `docs` の**両方**が空のときだけエラーにする。`runEpubAnalysis` は `order` が空なら `Sort: EPUB` の追加も `rebuildAfterReorder()` も行わず (`epubPageOrder = null` のまま)、本文リーダーだけ提供する
- **パス突き合わせ**: OPF/XHTML の href は percent-encode されていることがあるので、`epubResolve()` で decode + `../` 解決 + 小文字化した正規化キーで比較する (`epubNormPath()` がアーカイブエントリ名側の同等処理)
- **並び順の適用**: `sortSelect` に `Sort: EPUB` オプション (`#sortEpubOpt`、既定 `hidden`) を追加し、解析成功時に表示 + 選択。`applySortOrder()` の `epub` 分岐が `epubPageOrder` の rank で並べ替え、**読み順に現れない画像は末尾に Natural 順**で残す (ページが消えない)。`epubPageOrder` が無いのに `epub` が選ばれている場合は `natural` にフォールバック
- **パーサの堅牢化**: `epubParse()` は `DOMParser` の XML パースが `parsererror` を返したら HTML パーサで再試行する。XML 文書では CSS 型セレクタが名前空間非依存かつ大小区別ありなので `navLabel` 等がそのまま引ける
- **状態リセット**: `resetEpubState()` を `loadImageEntries` 冒頭と `loadPDF` で呼び、`epubEntries` / `epubPageOrder` / `epubToc` / `Sort: EPUB` / TOC タブを破棄する

### EPUB 目次 (TOC、`T` キー)
- 構造解析で目次が取れたときのみサイドバーに 3 つ目のタブ `TOC` が出現 (`T` キーで開閉、`.sidebar-tabs.has-toc` でタブのフォントを詰める)
- **EPUB3 nav を優先**: manifest の `properties="nav"` の XHTML から `<nav epub:type="toc">` の `<ol><li><a>` を辿る。階層は `ol`/`ul` の祖先数で算出しインデント表示
- **EPUB2 NCX にフォールバック**: `spine[toc]` → NCX、無ければ `application/x-dtbncx+xml` / `*.ncx` を探し `navMap > navPoint` を辿る。階層は `navPoint` の祖先数
- **ページ番号の解決**: spine を辿る際に「ドキュメント → そのページの先頭画像」を `docFirstImage` に記録。画像を持たない章は後続ページを引き継ぐ (逆順走査)。spine 経由で解決できなかった項目は、その XHTML を個別に読んで先頭画像を得る (manifest フォールバック時の目次用)
- 各項目に `p.N` を表示、クリックで `renderView(N)`。ページを解決できなかった項目は `.no-page` で淡色・クリック不可
- `updateTocActive()` が `renderView` / `updateScrollCurrentPage` から呼ばれてハイライトを更新する。**判定は `currentPage` ではなく「表示中の最大ページ」** (`Math.max(...getSpreadPages(currentPage))`) で行う: 見開きでは `currentPage` がペアの小さい方なので、右ページ始まりの章を選んでも1つ前の項目が光ってしまうため。さらに、クリックで飛んだ項目は `tocClickedEl` に保持し、そのページが表示範囲に残っている間はハイライトを維持する (同じペア内に複数の章があるとき、クリックした方を優先するため)。`tocClickedEl` は表示範囲から外れた時点と `renderToc()` で破棄

### リフロー型 EPUB の本文リーダー (`R` キー、comic-viewer.html のみ)
固定レイアウトでない EPUB は「ページ = 画像」に落とせないので、中身の XHTML をそのまま読ませる。**構造解析 (`E`) 済みであることが前提** (`epubDocs` が空なら案内トーストを出して何もしない)。

- **入口**: `R` キー (現在ページに対応する文書を `epubDocIndexForPage()` で選ぶ) / `TOC` タブ下部の `.toc-docs` セクション (「本文を読む」ボタン + 全文書リスト、`renderEpubDocList()`)。TOC タブは `epubToc` が空でも `epubDocs` があれば出す
- **モーダル** (`#epubReaderOverlay`): `<` / 文書プルダウン / `>` / `A-` `A+` (`epubFontScale` 70〜200%) / `幅: 標準` ⇄ `幅: 広い` / `原文CSS` チェック / `→ Page` (その文書の先頭画像ページへジャンプ)。Escape・背景クリックで閉じる。開いている間はキーボードの ←/→ が文書送りになる (`keydown` の先頭で分岐)
- **描画** (`epubBuildDocHtml()`): XHTML を DOMParser で読み、**外部リソースを一切参照しない自己完結 HTML 文字列**に書き換えて `iframe.srcdoc` に入れる
- **セキュリティ (多層防御)**:
  1. `<iframe sandbox="allow-same-origin">` — `allow-scripts` を与えないので EPUB 内の JS は実行されない。`allow-same-origin` は blob: URL を読ませるために必要 (スクリプトが動かないので同一オリジンでも文書側は何もできない)
  2. 注入する `<meta http-equiv="Content-Security-Policy">` が `EPUB_CSP` = `default-src 'none'; img-src blob: data:; style-src 'unsafe-inline'; font-src blob: data:; ...` — 書き換え漏れがあってもネットワークに出ない
  3. 書き換え時に `script/iframe/object/embed/audio/video/source/track/form/base/meta/noscript` を除去、`on*` 属性を除去、`http(s):` / `//` / `javascript:` 等の URL を落とす
- **リソース差し替え**: `epubImgByPath` (展開済み画像 Blob) / `epubByPath` (構造ファイル、`.svg` は `image/svg+xml` として Blob 化) から `URL.createObjectURL` し、`img/@src`・SVG `image/@xlink:href`・CSS の `url()` に差し込む。解決できない参照は `url("data:,")` に潰す (`about:blank` だと CSP 違反ログが出て紛らわしい)。`@import` は無条件で削除。生成した blob URL は `epubReaderBlobs` に貯め、次の描画時 (srcdoc 差し替え後) と閉じたときに revoke する
- **CSS**: 既定では EPUB のスタイルシート・インライン style を捨て、`epubBaseStyle()` の読みやすい暗色スタイルだけを当てる (暗色背景に原文の黒文字指定が残ると読めなくなるため)。`原文CSS` ON のときだけ `<link rel=stylesheet>` の中身をインライン化し、インライン style も url() 書き換えのみで残す (このとき配色は白背景・黒文字に切替)
- **幅トグル** (`#epubWidthToggle`, localStorage `viewerEpubReaderWide`): 標準 = モーダル `max-width:820px` + 本文 `44em`、広い = `1600px` + `78em`。本文の折り返し幅は `epubBaseStyle()` が注入する CSS 側にあるので、トグル時はクラス付け外しに加えて `epubRenderDoc()` で描き直す
- **縦書きは無効化**: `epubOverrideStyle()` が `*{writing-mode:horizontal-tb!important}` を最後に当てる。インライン style の `writing-mode` まで潰す必要があるので `html,body` ではなく `*` に当てている
- **リンク**: 同一文書内のフラグメント (`#...`) だけ残す。iframe 内に JS が無く他文書へ遷移させる手段がないため、それ以外の `href` は削除する
- **画像0の EPUB を開けるようにする仕組み**: ビューア本体は「1ページ = 1画像」を前提にしているため、`loadArchive` で `imageFiles.length === 0` かつ `hasEpubStructure(structFiles)` のときだけ `makeReflowPlaceholderPage()` が案内文入りの canvas を1枚合成して `loadImageEntries` に渡し、続けて `runEpubAnalysis(true)` で解析 → 本文リーダーを自動起動する。EPUB 構造を持たないアーカイブは従来通り「画像ファイルが見つかりません」

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

### ローディング表示フロー
- `showLoading("{ファイル名} を展開中...")` のオーバーレイは**初回ページ描画＋全サムネイル生成が完了するまで**表示し続ける (展開完了 → `renderView(1)` → `renderThumbnails()` 完了後に `hideLoading`)
- 実装: `onDocLoaded` は async で `await renderView(1)` 後、`docType === 'archive'` のときのみ `await renderThumbnails()` する。PDF (loading overlay 無し) は従来通り非 await でバックグラウンド生成
- 理由: 関連付け cold start 時、`renderThumbnails` がメインスレッドを 3 秒〜占有するため、トーストだけ先に消えると「フリーズした UI」と「中途半端な progress bar」がユーザーに見えてしまう (CSS transition がメインスレッド占有で更新できず止まる)。サムネ完了までトーストを残せば「展開中 → 即操作可能」のクリーンな遷移になる
- `loadPDF` / `loadImageEntries` も `await onDocLoaded()` する。`loadArchive` の `finally { hideLoading }` が全描画完了後に走る

### メインスレッド占有対策 (Page Unresponsive 防止、comic-viewer.html)
- **問題**: 少し大きめのアーカイブを展開すると、展開後の重いループがメインスレッドを長時間ブロックし、Chrome が「ページが応答していません (待機/離れる)」ダイアログを表示する。展開本体 (`archive.extractFiles()`) は libarchive Worker 側なので無関係。原因は展開後にメインスレッドで走る2つのループ:
  1. `loadImageEntries` — 全画像のデコード (`loadImageFromBlob`) + アニメ判定 (`isAnimatedImage` が大きい画像のバイト列を同期スキャン)
  2. `renderThumbnails` — 全ページのサムネイルを Pica で縮小描画 (上述の通り 3 秒〜占有)
- **対策**: `yieldToMain()` ヘルパーでループ途中に定期的にイベントループへ制御を返す。`scheduler.yield()` (対応ブラウザ) を優先し、非対応時は `new Promise(r => setTimeout(r))` フォールバック。これで Chrome の応答性チェックがリセットされダイアログが出なくなり、同時に progress bar / loading 表示も実際に更新されるようになる (従来はメインスレッド占有で CSS transition が止まって見えていた)
- **時間バジェット制御**: `yieldToMain()` は前回 yield から 40ms 未満なら即 return (`performance.now()` で計測)。`loadImageEntries` / `renderThumbnails` の各ループ先頭で `await yieldToMain()` を呼ぶが、小さいファイルでは追加待機がほぼ発生せず従来通り高速
- **重要**: 「ダミー出力で誤魔化す」のは無効。メインスレッドがブロックされている間は描画も出力も反映されないため、yield (真のマクロタスク境界) でしか解決しない
- pdf-viewer.html は対象外 (PDF サムネイル描画は PDF.js Worker 経由で本質的に非同期 yield するため同様の問題は起きにくい)。必要なら同じヘルパーを移植可能

### 実行要件
- ローカル HTTP サーバー必須 (`python -m http.server`, `php -S localhost:8000` 等)。**pdf-viewer.html も同様** (`file://` では `vendor/` の ES モジュール import が CORS でブロックされ、PDF.js / Pica が読めない)
- `file://` では ES モジュールの import / WASM Worker / Service Worker のいずれも動作しない
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
- E キー: EPUB 構造解析を実行 (comic-viewer.html のみ、詳細は「EPUB 構造解析」セクション)
- T キー: EPUB 目次 (TOC) サイドバーの開閉 (comic-viewer.html のみ、構造解析後に有効)
- R キー: EPUB 本文リーダーを開く (comic-viewer.html のみ、構造解析後に有効。リーダー表示中は ←/→ が文書送り)
- O キー: サーバーのライブラリを開く (`library.php` 設置時のみ有効。**ファイル未読み込みでも使うので `totalPages === 0` / `!pdfDoc` ガードより前に置くこと**。詳細は「ライブラリ参照機能」)
- Escape: UI再表示 (UI表示中に2秒以内にもう一度押すと `location.reload()` でファイルを閉じてドロップ画面に戻る。1回目押下時は「もう一度 ESC で閉じる」トーストを2秒表示。モーダル (ヘルプ / GIFオーバーレイ / パスワード入力 / EPUB 本文リーダ) が開いているときはモーダル閉じが優先)
- ヘッダー左上のタイトル (`#appTitle`) タップ: `confirm()` ダイアログで確認の上 `location.reload()`。PWA でキーボード無し / 更新ボタン無しの環境からも明示的にファイルを閉じる手段
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

### プログレスバー (`.progress-bar` / `setProgress()`、両ビューア共通)
- **進行中の処理専用の一時インジケータ**。用途はアーカイブ展開 (`loadImageEntries`) / EPUB 構造解析 (`analyzeEpub`) / 一括保存 (`saveAll`) の3つだけ
- **読書位置インジケータとしては使わない**。以前は Scroll モードの `updateScrollCurrentPage()` が `currentPage / totalPages` を書き込んでいたが、Single/Spread では更新されないうえ、モードを抜けてもバー幅が残り続ける (例: 80% のまま)。UI 非表示 / 全画面では画面上端に青いバーだけが居座って邪魔になるため廃止した。ページ位置はヘッダーのページ番号と Thumbs で確認する
- **終了時は必ず `setProgress(0)`**。進行ループは `try { ... } finally { setProgress(0) }` で囲み、途中で例外が出てもバーが残らないようにする (`saveAll` はあわせて `saveAllBtn.disabled` も finally で戻す)
- `setProgress()` 自体も 0〜100 にクランプし、`NaN`/`Infinity` (例: `totalPages === 0`) は 0 として扱う

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
- Pica 初期化: `import { Pica } from './vendor/pica/pica.js'` → `new Pica({ features: ['js', 'wasm', 'ww'] })`
  - **v10 以降は default export がクラスではなくファクトリ関数 `pica(options)`** に変わっている。`export { Pica, pica as default }` なので、クラスを使うには**名前付き import が必須** (v9 までの `import Pica from ...` は壊れる)
  - **`'ww'` (Web Worker) を有効化済み** — 縮小処理がメインスレッドから外れる。combined build (`pica.min.mjs`) は worker を文字列で内包し blob URL で起動するので、`workerURL` 指定も split build も**不要**。COOP/COEP (`crossOriginIsolated`) 下でも問題なく動作する
  - **`ensurePicaReady()` で `capabilities.ww_offscreen_canvas = false` を強制すること (nodeca/pica#223 の workaround)**:
    - v10 の `__extractTileData` は `ww && ww_offscreen_canvas` が真だと tile を `transferToImageBitmap()` で worker へ渡す (`kind: 'bitmap'`)。Chrome はこの ImageBitmap 往復で**タイル境界を壊す** — 境界に 1px 幅の差 (高周波パターンで最大 27/255、なだらかな階調では最大 1/255) が出て、`concurrency > 1` では**実行ごとに結果が変わる** (毎回 600〜1400px が変化)。差分を増幅するとタイル境界の格子線と完全に一致する
    - これは **2021年に upstream が #223 として修正済みだった不具合の再発**。v7.1.1 のコミット `da292f78` "Force WW always return typed array (Chrome workaround)" で `returnBitmap = true` をコメントアウトしていたが、**v10 の書き直しでこの workaround が失われていた** (v10.0.2 には `#223` への言及も `returnBitmap` ガードも無かった)
    - **上流は 10.0.3 (2026-08-15) で部分的に修正** (issue #258 として報告 → "Restored lost workaround" + 回帰テスト追加)。worker 側 `resizeBitmap()` が結果を `transferToImageBitmap()` で返すのをやめ、`kind: 'bitmap'` のジョブでも**常に typed array を返す**ようになった。復帰路が array になると main 側は `drawImage` ではなく**内側領域を切り出す `putImageData`** を通るので、タイル境界のはみ出しは起きなくなった
    - **しかし往路 (main → worker) は 10.0.3 でも `transferToImageBitmap()` のままで、継ぎ目は残っている**。10.0.3 を Chrome で実測 (2048px 市松 → 900px、`ww_offscreen_canvas` を上書きしない場合): **7071px が非 worker 経路と非一致・最大差 66/255**。差分は `innerTileWidth` 境界 (この条件では x=444 / 888) の手前 `destTileBorder` (=3) 列、つまり **441-443 / 888-890 に集中**し、垂直な線として並ぶ。なだらかな階調では差 1/255 に収まるがスクリーントーン等の高周波では見える。**解消されたのは非決定性だけ** (同条件の2回実行はビット一致)。上書きを入れると差 0
    - したがって **`ensurePicaReady()` の上書きは 10.0.3 でも必須** (性能上の都合ではない)。なお 10.0.2 時代に array 経路が速かったのは復帰路の ImageBitmap が原因で、10.0.3 では両者の所要時間はほぼ同じ (単発 resize で 51ms / 51ms)
    - `ww_offscreen_canvas` を偽にすると `getImageData` による `kind: 'array'` 経路になり、**出力が非 worker 経路とビット完全一致** (差 0)、かつ決定的になる。この capability は `createCanvas()` では最終フォールバックにしか使われず document 環境では到達不能、別フラグの `offscreen_canvas` は真のままなので副作用は無い
    - `init()` 後に上書きする必要がある (feature detection が `init()` 内で値を書き込むため、事前設定では上書きされてしまう)。`init()` は `__initPromise` をキャッシュするので `resize()` 内部の再 init で戻ることはない。トップレベル await は cold start の launchQueue 処理を遅らせるので使わず、初回 `resize` の直前に `await ensurePicaReady()` する遅延ゲート方式にしている
  - 実測 (12ページ CBZ / 2000×2900 スクリーントーン / HQ ON、`fileInput` の change から全サムネイル生成完了まで):

    | 構成 | 所要 | 最長フレーム間隔 | long task >50ms | 出力 |
    |---|---|---|---|---|
    | worker 無効 | 2497ms | 316ms | 14回 | 基準 |
    | ww + bitmap 経路 | 2150ms | 70ms | 3回 | 継ぎ目あり・非決定的 |
    | **ww + array 経路 (採用)** | **1118ms** | **66ms** | 4回 | **基準とビット一致** |

    `yieldToMain()` によるメインスレッド占有対策と併用する
  - **更新手順**: npm パッケージ `pica` の tarball から `package/dist/pica.min.mjs` を `vendor/pica/pica.js` と `docs/webapp/vendor/pica/pica.js` にコピー (自己完結型 ESM、`glur` / `multimath` はバンドル済み)。先頭にバージョン明記のバナーコメントを付ける (minified 本体にはバージョン文字列が無いため)。**更新時は #223 / #258 系の回帰を必ず確認する**。復帰路は `grep -o 'postMessage({kind:"[a-z]*"' vendor/pica/pica.js` が `array` だけを返せば健全。往路まで直ったか (= `ensurePicaReady()` の上書きを外せるか) は grep では分からないので、`ww_offscreen_canvas` を上書きした場合としない場合の出力を実際に突き合わせて差 0 を確認すること
  - **注意**: Pica の既定フィルタは v8.0.0 以降 `mks2013` で、Lanczos3 ではない。`resize` に `filter` を渡していないので実際に効いているのは mks2013
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
- **依存ファイル**: `vendor/vips/vips-es6.js` (78KB) + `vendor/vips/vips.wasm` (4.8MB) — wasm-vips v0.0.18 / libvips 8.18.3
- **更新手順**: npm パッケージ `wasm-vips` の tarball から `package/lib/vips-es6.js` と `package/lib/vips.wasm` の2ファイルだけを `vendor/vips/` と `docs/webapp/vendor/vips/` にコピーし、`sw.js` の `CACHE_NAME` をインクリメントする。`vips-heif.wasm` / `vips-jxl.wasm` / `vips-resvg.wasm` は `dynamicLibraries: []` のため不要。v0.0.18 は WebAssembly Exception Handling が既定で有効 (Chrome 95+ / Safari 15.2+ / Firefox 131+ が必要)
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

### ネットワーク上のファイル (NAS / SMB) 対策 (両ビューア共通)
- **症状**: NAS (パスワード付き Samba) 上のファイルを PWA から開くと、展開の途中で `NotReadableError: The requested file could not be read...` が出て、"展開中..." のオーバーレイのまま**固まる**
- **原因**: `File` は実体のコピーではなく「パス + size + lastModified のスナップショット」で、実際の I/O は後から走る。その時点で SMB セッションが切れている (Windows の `autodisconnect` は既定15分アイドル / NAS 側のアイドルタイムアウト) / 再認証が要求された / mtime がずれた 等だと `NotReadableError` になる。ブラウザから SMB 資格情報は渡せないので**この失敗自体は OS/ネットワーク側の問題で回避できない**
- **固まる理由 (ライブラリのバグ)**: `vendor/libarchive/libarchive.js` の `open()` は `new Promise((res, rej) => { this.client.open(this.file, cb(res)) })` と書かれていて **reject を一切繋いでいない**。worker 側の読み込みが失敗しても Promise が settle せず、`await Archive.open(file)` で永久に待つ。呼び出し側の try/catch では捕まえられない (コンソールの `Uncaught (in promise)` はこの捨てられた rejection)
- **対策** (`readFileToBuffer()` / comic-viewer.html は `materializeFile()`):
  - **メインスレッドで自分でファイルを読み切ってから** libarchive / PDF.js に渡す。libarchive にはメモリ上の `File` を渡すので worker 側は実ファイルに触らない → 失敗を捕捉できて固まらない
  - `file.slice()` による 8MB チャンク読み + チャンク単位で最大6回リトライ (`FILE_READ_BACKOFF` = 300/700/1500/3000/5000/8000ms)。**失敗するたびに読み幅を半分に縮め** (`FILE_READ_MIN_CHUNK` 512KB まで)、以降のチャンクも縮めたまま読み進める。大きな読み出しほど SMB でタイムアウトしやすいため
  - **同じ `File` オブジェクトはいくら再試行しても復活しない** (実測)。0% から読み直しても必ず同じ位置で失敗する。壊れているのはネットワークではなく **その File 参照が握っている OS 側のファイルハンドル**で、JS から revalidate する手段が無い。エクスプローラで開き直すと先へ進むのは、そこで**新しいハンドルが作られる**から。したがって対策は次の2本立て:
    1. **`FileSystemFileHandle` を保持し、失敗したら `handle.getFile()` で File を取り直す** (`refreshFileFromHandle()`)。これがプログラムから「開き直す」ことに相当し、**1回の open のまま先へ進める**。ハンドルが取れる経路は launchQueue (ファイル関連付け) / ドラッグ&ドロップ (`DataTransferItem.getAsFileSystemHandle()`、DataTransfer はイベント内でしか有効でないので `await` 前に呼ぶ) / `showOpenFilePicker()`。`<input type=file>` では取れない
    2. **`pendingRead` に読み込み途中のバッファを残す** (`{key, bytes, offset}`、key = `name|size|lastModified`)。ハンドルが無い経路でも、ユーザーが同じファイルを選び直すたびに**続きから読み進む** (28% → 57% → 85% → 完了)。成功したら破棄
  - 全チャンク失敗時は `FileReadError` を投げ、`showFileReadErrorToast()` が `{ファイル名} を N% まで読み込みました…` とタップ可能なトーストを出す (12秒)。`retryOpenFile()` が、ハンドルがあれば無言で再試行、無ければ `showOpenFilePicker()` (非対応環境は `fileInput.click()`) でファイルを選び直させる
  - 読み込み中は progress bar + オーバーレイに `{ファイル名} を読み込み中... N%` (4MB 超のときのみ)。再試行中は `— 再試行中 N/6` を付けて「止まっていない」ことを示す (オーバーレイの無い PDF 側はトーストで表示)
- **副次効果**: cbz/zip/epub はこれまで `buildFilenameMap` (メインスレッド) と libarchive worker で**同じファイルを2回フルリード**していたのを1回に削減。`buildFilenameMapFromBuffer()` が読み込み済みバッファを直接受ける (`buildFilenameMap` は入れ子アーカイブ用にラッパーとして残す)
- **注意**: メモリ上の `File` を渡すので `loadArchive` の引数は `srcFile` (元の File) と `file` (メモリ上の File) を使い分ける。名前・サイズは同じなのでファイルハッシュ (しおり) は変わらない

### メモリ管理 (黒画面=canvas/GPUメモリ枯渇 対策、両ビューア共通)
- **症状**: インストール済み PWA (standalone) で、ページを送っていくと N ページ目以降が**例外を出さず黒画面**になる (Chrome タブでは GPU 予算に余裕があり再現しにくい)。`page.render()` は成功 resolve するのに中身が黒い canvas が返る = canvas/GPU メモリ確保の無言失敗。HQ ON だと1ページあたりの canvas 生成量が多く、より早い番号で限界に達する
- **原因**: ①PDF.js はページを表示するたびデコード済み画像/オペレータリストを内部キャッシュに保持し続ける ②差し替えた古い canvas の backing store を明示解放しないと GC まで GPU メモリに残る。両者がページ送りで累積して上限に当たる
- **対策ヘルパー** (`renderedPageNums` Set でメインビュー描画ページを追跡):
  - `cleanupPdfPagesExcept(keepSet)` — 表示中以外のページの `page.cleanup()` を呼び PDF.js キャッシュを解放 (`renderView` の描画後に呼ぶ)。アーカイブ時 (`pdfDoc===null`) は no-op
  - `renderThumbnails` も各サムネ描画後に `page.cleanup()` (bitmap は canvas に残るので解放してよい)。読み込み時に全ページ分が蓄積するのを防ぐ
  - `releaseCanvases(root)` / `releaseCanvasList(list)` — canvas の `width=height=0` で backing store を即時解放
  - HQ パスの中間 `fullCanvas` (画面に出さず pica/vips が読み戻すだけ) は `getContext('2d', { willReadFrequently: true })` で CPU バッキングにし GPU 予算から外す + 使用後に即解放
- **ダブルバッファリング** (暗転=チカチカ 防止): `renderView` は描画前に `viewer.innerHTML=''` で消すと、新ページの非同期レンダリング完了まで空白フレームが見えて暗転する。代わりに**新ページを `newNodes[]` に組み立ててから `viewer.replaceChildren(...newNodes)` で原子的に差し替え**、旧 canvas は差し替え後に `releaseCanvasList()` で解放する。これでメモリ解放を有効にしたまま暗転が出ない
- **`FREE_PAGE_CACHE` 定数** (各HTML先頭、デフォルト `true`): メモリ解放処理 (`releaseCanvases`/`releaseCanvasList`/`cleanupPdfPagesExcept`) の有効/無効を1箇所で切替。`false` で全解放を no-op 化 (暗転皆無だがメモリ累積)。解放は常に描画後なので `true` でも暗転は出ない

### リサイズ振動ループ対策 (Spread+Fit+特定解像度で再描画暴走、両ビューア共通)
- **症状**: Spread + Fit + 特定ウィンドウ幅で `renderView` が無限再描画され、canvas を量産してメモリ増大→黒画面。ウィンドウ幅を変えると直る
- **原因**: `getScale()` の幅計算は `window.innerWidth` (縦スクロールバー幅**込み**)、ヘッダーの実レイアウトは `clientWidth` (スクロールバー幅**除く**) で約15px ずれる。Spread+Fit でコンテンツ高さがビューポート境界付近にあると縦スクロールバーが出入りし、その15px で `.header` の `.controls` (`flex-wrap`) が折り返したり戻ったりしてヘッダー高さが2値振動 → `maxH` 変化 → スケール変化 → スクロールバー再トグル … の自己持続ループ。ResizeObserver の `h !== lastHeaderH` ガードは2値振動には無力
- **対策**:
  - **`html { scrollbar-gutter: stable; }`** (根本対策) — スクロールバー領域を常時確保し、出入りによる幅変動を排除。ヘッダーが再折り返ししなくなりループの起点が消える
  - ヘッダー ResizeObserver の再描画を `requestAnimationFrame` でコアレス (保険)
  - `window.resize` の再描画を 120ms デバウンス (ドラッグリサイズ中の canvas 量産抑制)

### しおり（ブックマーク）機能
- サイドバーを「Bookmarks」「Thumbs」の排他タブに分割
- localStorage にファイルハッシュ (SHA-256先頭16文字、`file.name + '|' + file.size`) とページ番号を保存
- **ハッシュ計算は `sha256Hex()` に集約 (両ビューア共通)**。`crypto.subtle` は **secure context (HTTPS / localhost) にしか存在しない**ため、iOS で LAN の `http://192.168.x.x:8000` を開くと `undefined is not an object (evaluating 'crypto.subtle.digest')` になる。しかも `openFile` / `openPdfFile` の**冒頭**で `generateFileHash` を呼んでいるので、しおり無効でも、ライブラリ経由でもローカルのドロップでも**ファイルが一切開けなくなる**。そのため `crypto.subtle` が無い環境では純 JS 実装 `sha256HexJS()` (FIPS 180-4 そのまま、K定数は `SHA256_K`) にフォールバックする。**出力は `crypto.subtle` とビット単位で一致する**ので、しおりは HTTPS 環境・他端末とそのまま共有できる (Node の `crypto` および Chrome の `crypto.subtle` と padding 境界 55/56/63/64/65 バイト含めて照合済み)。呼び出し側は `(await sha256Hex(...)).substring(0, 16)`。使用箇所は `generateFileHash` / 二重アーカイブの再ハッシュ (comic) / `libHashNameSize` (ライブラリの既読バッジ)
- なお http のままでは Service Worker が登録できない (`navigator.serviceWorker` 自体が無いので `comic-viewer.html:29` のガードで no-op) ため、PWA インストール・オフライン動作・COOP/COEP (wasm-vips) は使えない。ファイルを開いて読むことはできる
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

### 保存形式 (formatSelect、両ビューア共通)
- 選択肢: PNG / JPEG 95% / **WebP 95% (デフォルト)** / Clipboard (View) / Clipboard (Page)
- **デフォルトは WebP** (`<option value="image/webp" selected>`)。`getExt()` / `getQuality()` が MIME から拡張子・品質を導出 (PNG は quality `undefined`、JPEG/WebP は 0.95)

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
- **Gapless チェックボックス**: Scroll モード時のみヘッダーに表示 (R2L/L2R セレクトは Scroll では無意味なので非表示にし、同じ位置に出す)
  - ON で `.scroll-container` に `gapless` クラスを付与し、ページ間の `gap (16px) / padding (16px) / box-shadow / page-label` をすべて 0/none/非表示 にしてピクセル境界なしの連結表示 (Webtoon 風)
  - トグル時は再レンダリング不要 (CSS クラス付け外しのみ)
  - `updateScrollControls()` が viewMode の change で `bindDir` ↔ `gaplessLabel` の表示を切替
- **新ドキュメント読み込み時の状態リセット** (重要): `renderView` の Scroll 分岐は `.scroll-container` の存在で再構築要否を判定する最適化が入っているため、`loadPDF` / `loadImageEntries` で `pdfDoc` だけ差し替えると DOM/observer に前ファイルの状態が残り、古いプレースホルダに新 pdfDoc のページが 1 枚挿入されるバグが出る (10MB以上のPDFで再現しやすい)。各 load 関数のドキュメントロード成功直後に明示的に `scrollObserver.disconnect()` + `scrollRendered.clear()` + `viewer.innerHTML = ''` でリセットする (エラー時は手前で例外が飛ぶので前の表示は維持される)

### 色調補正フィルター (Filter、両ビューア共通)
- ヘッダーに **Filter** ボタン + ポップアップ
- **CSS フィルター** (即時適用): Brightness (50-150%), Contrast (50-150%), Gamma (0.20-3.00), Sepia (0-100%), Invert (0-100%)
- **ガンマ補正** (Gamma、即時適用): CSS の `filter` にガンマ関数が無いので、`<body>` 直後にインラインで置いた SVG フィルター (`#gammaFilter`) を `filter: url(#gammaFilter)` で参照して実現する
  - スライダーは整数 20-300 で管理し /100 して表示 (`1.00` = 無補正)。`feFuncR/G/B` の `exponent` に **`1/γ`** を書き込むので、1.00 より大きい値で中間調が明るくなる (画像のガンマ補正と同じ向き)
  - `color-interpolation-filters="sRGB"` が必須。SVG フィルターの既定は linearRGB で、そのままでは CSS の brightness/contrast (sRGB 上で動作) と食い違う。指定すると出力は理論値どおりになる (実測: 128 → γ2.2 → 186 = `(128/255)^(1/2.2)*255`)
  - フィルタ領域は `x=-2% y=-2% width=104% height=104%`。既定の 120% は Scroll モードの巨大な `.viewer` では無駄が大きく、かといって 100% ちょうどだと `.page-container` の `box-shadow` が切れる
  - `parts` の**先頭**に積む (元画像にガンマ → その結果に brightness/contrast が効く)。γ が 1.00 のときは `url()` 自体を積まない (参照フィルターは合成コストが高いため)
  - 他の CSS フィルターと同じく**表示のみ**で、エクスポート (`exportPageCanvas`) やクリップボードコピーには乗らない
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
  - 保存データ: `{ b, c, s, i, g, sh, shr, sht }` (旧プリセットとの後方互換: `g` 未設定時は 100 = 1.00、`sh/shr/sht` 未設定時はデフォルト値にフォールバック)
  - Save ボタンで現在のスライダー値を保存、Load ボタンで復元・即時適用
  - 未保存スロットの Load ボタンは disabled、保存済みスロットはツールチップに設定値を表示

### UI 設定の永続化 (localStorage、両ビューア共通)
- `viewerViewMode` — Single / Spread / Scroll の選択状態 (起動時に復元、変更時に保存)
- `viewerHQ` — HQ チェックボックスの状態 (`'1'` で ON、未設定で OFF)
- `viewerScrollGapless` — Scroll モードの Gapless チェックボックス状態 (`'1'` で ON、未設定で OFF)
- `vipsEnabled` — wasm-vips 有効化フラグ (HQ engine トグル)
- `viewerEpubReaderWide` — EPUB 本文リーダの幅 (`'1'` で広い、未設定/`'0'` で標準)
- `viewerLibraryView` — ライブラリの表示 (`'grid'` でサムネイル、未設定/`'list'` でリスト)
- `libraryAvailable` / `libraryUnavailableAt` — ライブラリ機能の利用可否キャッシュ (詳細は「ライブラリ参照機能」)
- `viewerFilterPresets` — Filter プリセット 3 スロット
- ブックマーク系キー (ファイルハッシュ → bookmark オブジェクト)

### ヘルプモーダル (?、両ビューア共通)
- `?` キーまたはヘッダーの **?** ボタンでモーダル表示
- キーボードショートカット・マウス/タッチ操作の一覧を表示
- Escape または背景クリックで閉じる
- pdf-viewer.html はテキストモード操作の説明も含む

### ライブラリ参照機能 (`library.php` + 両ビューア共通の Library モーダル)
サーバーに設置したときだけ有効になる「サーバー上の指定フォルダを一覧・検索して開く」機能。ローカルファイルを開く従来の経路は一切変更していない。

#### サーバー側 (`library.php`)
- 設定は `library.config.php` (`library.config.example.php` をコピー、`.gitignore` 済み)。`root` / `label` / `auth` / `exts` / `maxEntries` / `maxDepth` / `followSymlinks` / `fsEncoding`
- エンドポイントは4つだけ (すべて GET):
  - `?action=ping` — 疎通確認 (`{ok:true, root, exts}`)
  - `?action=tree` — **ルート以下の対象ファイルを1回で全件返す**。数百件想定なのでページングも検索 API も持たない。フォルダ構造は `path` からクライアント側で導出する (空フォルダは出ない)。表紙があるエントリにだけ `cover: true` が付く
  - `?action=file&path=...` — 本体をストリーム配信。Range (206) 対応、`Cache-Control: no-store`
  - `?action=cover&path=...&w=320` — 表紙画像を配信 (下記「表紙画像」)
- `file` / `cover` のパス検証は `lib_request_path()` に集約。検証に落ちたら `lib_fail()` が exit するので、戻り値は常に安全なパス
- **パス検証は `realpath` 後の前方一致**。文字列の `..` 除去だけではシンボリックリンク経由で root 外に出られるため。`action=file` では basename だけでなく**途中のフォルダも**ドット始まりを弾く (`.hidden/book.cbz` を直接要求されるため。`tree` 側は走査時に弾いている)
- ファイル名は UTF-8 (NFC) に正規化して返す。`fsEncoding` は「`scandir` が UTF-8 以外を返す環境」のための保険で、**既定の `''` のままが正しい**
  - **Windows 版 PHP でも `''` でよい** (PHP 8.4 / Windows で実測)。PHP 7.1 以降は `default_charset=UTF-8` ならワイド文字 API を使うので `scandir` は UTF-8 を返す。ここで `'SJIS-win'` を指定すると**二重変換で文字化けし** (`漫画テスト.cbz` → `貍ｫ逕ｻ繝?せ繝?`)、しかも 0x86 等が `?` に潰れて非可逆なので `action=file` / `action=cover` が 404 になる (ユーザー報告 → 再現・修正済み)
  - そのため `lib_to_utf8()` は **すでに妥当な UTF-8 なら変換しない** (`lib_is_utf8()`)。誤って `SJIS-win` を設定しても壊れない
  - 逆方向も対称にしてある: `lib_resolve_request()` が **まず UTF-8 のまま `realpath` を試し、外れたときだけ `lib_from_utf8()` で変換して再試行**する (`action=file` / `action=cover` 共通)
  - `'SJIS-win'` を設定するのは、一覧が化けるか `tree` の `warnings` に「UTF-8 として解釈できないファイル名を N 件除外しました」が出たときだけ
- macOS (NFD) 対策として `lib_resolve()` は realpath 失敗時に FORM_D で再試行する

#### 表紙画像 (サイドカー方式、自動生成しない)
- 命名規則は **`<元のファイル名><coverSuffix>.<画像拡張子>`** (既定 `vol01.cbz.coverimage.webp`)。設定で `coverSuffix` / `coverExts` を変更可
  - **元のファイル名を丸ごと残す**のが要点。`vol01.cbz` と `vol01.pdf` が同居しても衝突せず、実拡張子があるので Content-Type を推測せずに済み、ファイルマネージャでも元ファイルの真下に並ぶ
- `lib_walk()` はディレクトリごとに `小文字名 → 実際の名前` の索引を作り、そこから表紙の有無を判定する。**stat を撃たない**のが重要 (ネットワークストレージで数百件 × 6 拡張子の `is_file()` を撃つと目に見えて遅くなる)
- `action=cover` が受け取る `path` は**本のパス**で、表紙のファイル名はサーバー側で導出する。クライアントから画像パスを受け取らないので、表紙経由で任意ファイルを読ませる余地が無い。加えて表紙画像自体の拡張子 (`.png` 等) は `exts` に無いので `file` でも `cover` でも直接は取れない
- `w=` があり GD が使えて元画像がそれより大きいときだけ縮小する (`lib_resize_image()`、WebP 優先 / 無ければ JPEG)。GD 無し・未対応形式・4000万画素超は原本をそのまま返す
- **表紙だけはキャッシュを許可する** (`Cache-Control: private, max-age=604800` + ETag)。本体は `no-store` だが、表紙まで毎回取り直すとサムネイル表示のたびに全件再取得になる。SW は `library.php` を素通しするので、ここで指定したヘッダーがそのままブラウザキャッシュに効く。ETag には `w` も混ぜてあるのでサイズ違いは別エントリになる
- **表紙そのものを作るのは `tools/generate_coverimages.php`** (下記)。`library.php` は配信専用で、リクエスト経路では一切生成しない

#### 表紙生成ツール (`tools/generate_coverimages.php`、CLI 専用)
`library.php` が配信する表紙サイドカーを一括生成するスタンドアロン CLI。**Web から叩かれても `PHP_SAPI !== 'cli'` で 403 を返して何もしない** (`tools/` を DOCUMENT_ROOT 下に置いてしまった場合の保険)。

- **設定**: `library.config.php` をそのまま読む (`root` / `exts` / `coverSuffix` / `coverExts` / `maxDepth` / `followSymlinks` / `fsEncoding` を共有)。ツール固有の既定は `'coverTool' => [...]` で上書きでき、コマンドラインオプションが最優先。`library.config.php` が無くても `--root=PATH` だけで動く
- **表紙の決め方** (ここがビューアと揃っている必要がある):
  - PDF … 1 ページ目をレンダリング
  - EPUB … `comic-viewer.html` の `analyzeEpub()` を移植 (container.xml → OPF → spine → XHTML の `<img>` / `<svg><image xlink:href>` / インライン `style` の `url()`) して**読み順の 1 枚目**を採る。spine で取れなければ manifest の `image/*` 記述順、それも駄目ならファイル名順。`--epub-cover=metadata` なら OPF の `properties="cover-image"` / `<meta name="cover">` を先に見る (取れなければ spine にフォールバック)
  - 書庫 … ファイル名順の 1 ファイル目。既定 `lexical` / `--sort=natural` で `naturalCompare` 相当に切替 (どちらも comic-viewer.html の Sort と同じ規則)
  - `__MACOSX/` と `._` 始まりのエントリは除外。先頭候補がデコードできなければ次の候補へ (`maxCandidates` 枚まで)
- **書庫アクセス**: ZipArchive (cbz/zip/epub) → 7z → unrar の順に**一覧が取れたものを使う**ので、拡張子が偽装されていても (中身が rar の `.cbz` 等) 拾える。外部コマンドは `proc_open` の**配列形式**で起動しシェルを経由しない (空白・`%`・引用符を含むパスで壊れない)。7z/unrar からはパイプ経由で stdout に取り出すので一時ファイルを作らない
- **PDF レンダラ**: `imagick` → `pdftoppm` → `mutool` → `magick` → `gs` を順に試し、**成功したエンジンを記憶して 2 件目以降は直行**する。全滅時は理由を並べて 1 件失敗として続行 (他の形式は処理される)
  - **外部レンダラの出力は必ず一時フォルダのファイルで受け取る** (`cov_temp_dir()` → 読み込み → `cov_rm_tree()`)。stdout 経由は移植性が無く、**Windows 版 pdftoppm 26.x は出力先の `-` を stdout ではなくファイル名として扱い、カレントに `-.png` を作って stdout には何も出さない** (実測)。テキストモードで改行が化ける危険もある
  - `magick` は PDF を自前で描けず Ghostscript を呼ぶので、gs 未導入の環境では `FailedToExecuteCommand gswin64c.exe` で失敗する。auto では `pdftoppm` が先に来るので通常は問題にならない
- **外部コマンドの検出 (`cov_find_bin()`)**: PATH (Windows は PATHEXT も) を**自前で走査してファイルの有無を見るだけ**。存在確認のために試し起動してはいけない — 引数を認識しないコマンドが対話モードに入り、`--check` がそこで固まる (`magick -v` が実測で該当)。Windows 向けに `C:\Program Files\...` の定番の場所も glob で見る (`gs/*/bin/gswin64c.exe` 等)
- **縮小**: `maxWidth` / `maxHeight` を超える画像だけ縮小 (拡大はしない、0 で無制限)。Imagick があれば `thumbnailImage`、無ければ GD の `imagecopyresampled`。出力形式が書き出せない環境では自動的に JPEG/PNG に落とし、**その拡張子が `coverExts` に無ければ起動時にエラー**にする (library.php が表紙として認識できないため)
- **スキップ**: 既存の表紙が `coverExts` のどれかで見つかれば飛ばす。`--force` で全再生成、`--stale` で「表紙が元ファイルより古いものだけ」再生成
- **`--mtime`**: 表紙の更新日時を**抽出元ファイル (PDF / EPUB / 書庫そのもの) の mtime** に合わせる。書庫内画像のタイムスタンプではない。スキップしたファイルにも適用されるので、後から `--mtime` だけ付けて回すこともできる
- **書き込み**: 同じフォルダの `.covertmp_*` に書いてから `rename`。ドット始まりなので `lib_walk()` にも引っかからない。書き終えたら**別拡張子の古い表紙を消す** (`coverExts` の優先順で古い方が拾われ続けるのを防ぐ)
- その他: `--dry-run` / `--check` (使えるバックエンド一覧) / `--filter` / `--path` / `--ext` / `--limit` / `--console-encoding=SJIS-win` (Windows コンソールの文字化け対策)。未知のオプションは黙って無視せずエラーにする

#### 表紙生成ツール Python 版 (`tools/generate_coverimages.py`)
**Windows の PHP は Imagick / Ghostscript を入れるのが面倒**なのに対し、Python は `pip install pillow pypdfium2` だけで PDF レンダリングまで揃う。Linux ではどちらでもよい。**PHP 版と表紙の決め方・オプション名・出力を揃えてあるので、片方を直したらもう片方も追うこと。**

- **`library.config.php` は読まない** (PHP を経由せず使えるようにするため)。設定は `--root` 等のオプション、または `--config=cover.json` (JSON、キー名は PHP 版の `coverTool` と同じ camelCase + `root` / `exts` / `coverSuffix` / `coverExts`)
- **バックエンド**: 画像は Pillow (必須)。PDF は `pymupdf` → `pypdfium2` → `pdftoppm` → `mutool` → `magick` → `gs`、書庫は `zipfile` (標準) / `py7zr` / `rarfile` → 外部 `7z` / `unrar`。**pip で入る範囲だけで PDF まで完結する**のが PHP 版との最大の差
  - `import fitz` は PyMuPDF 1.24.3+ で deprecation 警告を出すので `import pymupdf` を先に試す
  - **py7zr 1.x は `read()` が廃止され `extract()` だけになった**ので、`getattr(sz, "read", None)` で 0.x と分岐し、1.x では一時フォルダに 1 件だけ展開して読む
- **ZIP のファイル名順**: UTF-8 フラグ (`flag_bits & 0x800`) が無い ZIP は Python が cp437 でデコードするので、**生バイトに戻してからソートキーにする**。PHP 版 (バイト列ソート) と同じ順序を保つため
- ファイル名は Python が Unicode で扱うので `fsEncoding` 相当の設定は不要。Windows の cp932 コンソール対策は `sys.stdout.reconfigure(errors="replace")` で行う (`--console-encoding` で明示指定も可)
- 外部レンダラの出力を一時ファイルで受ける理由、コマンド検出で試し起動しない理由は PHP 版と同じ

#### Basic 認証 (`lib_require_auth()`)
設定の `'auth' => ['user'=>..., 'pass'=>..., 'realm'=>...]` があるときだけ有効 (既定 `null` = 認証なし)。**サーバー設定が不要で、認証の影響範囲が `library.php` に閉じるのが利点** — `.htaccess` でアプリ全体に掛けてしまうと SW のプリキャッシュが 401 で静かに壊れるが、この方式ならその事故が構造的に起きない。設定読み込み直後・ファイルに触れる前に呼ぶ。

- `header('WWW-Authenticate: ...')` を送ると **PHP が自動でステータスを 401 にする** (`main/SAPI.c` の `sapi_header_op` の特殊処理、PHP 8.4 で実測確認済み)。依存したくないので `http_response_code(401)` も明示している
- **`$_SERVER['PHP_AUTH_USER']` は SAPI 依存**。mod_php / nginx+php-fpm / ビルトインサーバーでは埋まるが、**Apache + CGI / FastCGI / php-fpm では Apache が `Authorization` を CGI 環境から意図的に落とすため埋まらない**。放置すると「正しいパスワードを入れても通らず認証ダイアログが無限に出る」という分かりにくい症状になる。そのため `lib_basic_credentials()` が `HTTP_AUTHORIZATION` / `REDIRECT_HTTP_AUTHORIZATION` からの base64 復号にフォールバックする (それでも届かない環境向けに `CGIPassAuth On` / `SetEnvIf` の案内を `library.htaccess.example` に記載)
- 比較は `hash_equals()` (平文でもタイミング差を出さない)。`pass` が `$` 始まりなら `password_hash()` 済みとみなして `password_verify()` で照合する
- 401 のボディは JSON。クライアントは status で先に判定するので実際には使わないが、curl 等から見たときのために揃えてある
- **クライアント側の挙動**: 同一オリジンの `fetch` に対して Chrome はネイティブの認証ダイアログを出し (実測: 401 を受けた fetch は pending のままダイアログ待ちになる)、入力後は透過的に成功する。以降は資格情報がキャッシュされ自動送信される。キャンセルされた場合は 401 が fetch に届き `libFetchJson` / `libDownload` が「ライブラリの認証に失敗しました」を出す。**起動時に probe を投げない設計**なのは、これを起動直後に出さないため

#### ⚠️ Service Worker の除外 (必須)
`sw.js` の fetch ハンドラ冒頭で `library.php` を **`return` して素通し**している。理由は2つあり、どちらも致命的:
1. キャッシュ参照が `cache.match(req, { ignoreSearch: true })` なので、`?action=tree` と `?action=file` が **`library.php` という同一キーに衝突**する (一覧を要求したのに本の中身が返る)
2. 開いた本が丸ごと Cache Storage に永久保存され容量が際限なく増える

#### クライアント側 (両ビューアで同一コード)
- 差分は先頭2行だけ: `LIB_ACCEPT` (comic は全形式 / pdf は `/\.pdf$/i`) と `libOpenDownloaded` (`openFile` / `openPdfFile`)。**片方を直したらもう片方にも同じ差分を当てること**
- 取得した Blob を `new File([blob], base, {lastModified})` に包んで既存の `openFile()` / `openPdfFile()` に渡すだけ。以降は通常のローカルファイルと完全に同じ経路 (PDF / アーカイブ判定・しおり・EPUB 解析・フィルタすべてそのまま動く)。メモリ上の File なので `readFileToBuffer` のリトライは即成功する
- `libDownload()` は転送中断時に **Range で受信済みの続きから再開**する (`LIB_BACKOFF`)。Range を投げたのに 200 が返る (サーバーが部分取得非対応) 場合はバッファを捨てて最初からやり直す
- **既読バッジ**: しおりのハッシュが `ファイル名|サイズ` なので、`tree` の `size` から一覧描画前に既読状態を引ける (`libComputeReadState`)。ローカルで開いた同じファイルのしおりとそのまま共有される
- **更新ボタン (⟳) は現在のフォルダ・絞り込み・スクロール位置をすべて保持する**。`libLoadTree(true)` が無条件に `libCwd = ''` していた頃は「絞り込みは残っているのにルートに戻る」という中途半端な状態になっていた。新しい `tree` にそのフォルダが1件も無いときだけルートに戻す (フォルダごと消えた場合)
- **表示切替** (`libView`、localStorage `viewerLibraryView`): リスト ⇄ サムネイル (グリッド)。`libRender()` が `libView` で分岐して行 / タイルを作る。グリッドの `<img>` は `loading="lazy"` なので画面外のタイルは取りに行かない
- **表紙プレビュー** (`libAttachPreview()`): `cover: true` の項目にだけ仕込む。マウスは `mouseenter` から 320ms 遅らせて出す (一覧を横切っただけで出ないように)、`mousemove` で追従。タッチは 450ms のロングタップで画面中央に大きく出す
  - ロングタップ後に指を離すと `click` が飛んでファイルが開いてしまうので、`libSuppressClick` を立てて `libRender()` 内の `openEntry()` が握り潰す。`touchstart` のたびに false に戻す
  - `touchmove` が 12px を超えたらスクロール操作とみなしてロングタップを取り消す
  - CSS で `user-select: none` / `-webkit-touch-callout: none` を当て、OS のテキスト選択・コールアウトが割り込まないようにする
  - 表紙が消えている等で `<img>` が `error` になったら、そのエントリの `cover` を落として以後は出さない (グリッドは拡張子プレースホルダに差し替える)
  - リストのアイコンは表紙があれば 🖼 / なければ 📄。ホバーで見られることの目印を兼ねる。**小さい絵文字どうしでは区別が付かない**ので、CSS で差を付けている: 表紙あり = `.lib-icon.cover` (青リング + 濃紺のチップ)、表紙なし = `.lib-icon.plain` (`grayscale(1)` + `opacity: 0.45`)。チップは 20×18px に収めてあり行の高さは変わらない
- **利用可否の判定**: 起動時の probe はしない (Basic 認証のダイアログが起動直後に出てしまうため)。ヘッダーの `Library` ボタンは常時表示し、初回クリックで 404 / 503 / **JSON でないレスポンス** (PHP が動いていないサーバーが `library.php` のソースをそのまま 200 で返すケース) を踏んだら `libraryUnavailableAt` を localStorage に記録して24時間だけ UI を隠す。後から設置されれば自動的に復活する。dropzone の導線は「一度つながった実績がある」(`libraryAvailable`) ときだけ出す
- **`?lib=<相対パス>`**: 起動時に自動オープン。**URL には書き戻さない** — `?lib=` が残っていると ESC 2回 (`location.reload()`) でファイルを閉じたつもりが再び開いてしまうため、`?share=1` と同様に `history.replaceState` で即座に取り除く。launchQueue / share_target が先に走った場合は `window.__libSkipAuto` で抑止する
- **`O` キー**: ファイル未読み込みでも使うので、keydown ハンドラの `totalPages === 0` / `!pdfDoc` ガードより**前**に置く。モーダルが開いている間は Escape で閉じるだけにして、絞り込み入力を邪魔しない

#### 仕様上の限界 (ドキュメントに明記済み)
- `root` を DOCUMENT_ROOT 外に置けば直リンク URL が存在しなくなるので、ブラウザからの直接ダウンロードは不可能にできる
- ただし API は HTTP なので **curl で直接叩くのは防げない** (Referer / Sec-Fetch-* は偽装可能)。制限したいなら Basic 認証等が必須
- 表示できたファイルは既存の Save 機能で保存できる。DRM ではない
- 認証は `library.config.php` の `'auth'` を使うのが既定の推奨 (上記)。`.htaccess` 等サーバー設定側で掛ける場合は **`library.php` にのみ掛ける**。アプリ全体に掛けると SW の `PRECACHE_URLS` 取得が 401 で静かに失敗し (`sw.js` は catch して warn するだけ)、オフライン動作が壊れる
- Basic 認証は資格情報を毎リクエスト base64 で送るだけなので、公開するなら HTTPS 必須

### 関数
- `getSpreadPages(pageNum)` — スプレッド構成を返す ([left, right] or [single])
- `canonicalPage(pageNum)` — ページ番号をペアの先頭に正規化
- `prevPageNum()` / `nextPageNum()` — ナビゲーション計算

## PWA / Service Worker

### `sw.js`
- **`CACHE_NAME`**: バージョン文字列 (現在 `pdf-viewer-v39`)。**アセット更新時は必ず番号をインクリメント**してユーザーに新キャッシュを配信する
- **`SHARE_CACHE`**: `share-stash-v1` — Web Share Target で受信したファイルを一時保存する専用キャッシュ (activate 時も削除対象外)
- **`PRECACHE_URLS`**: インストール時に一括取得するリソース (HTML 2種、vendor/ 配下全ファイル、manifest、icons)。`fetch(url, { cache: 'reload' })` でブラウザキャッシュをバイパス
- **`activate`**: `CACHE_NAME` と `SHARE_CACHE` 以外の旧キャッシュを削除し `self.clients.claim()`
- **fetch 戦略**: 同一オリジン GET に対してのみ cache-first。キャッシュヒット時も `withCoiHeaders()` で COOP/COEP/CORP ヘッダーを付与してから返す。キャッシュミスはネットワーク取得＋成功時は自動キャッシュ
- **`library.php` は除外 (必須)**: fetch ハンドラ冒頭で `return` して素通しする。`ignoreSearch: true` のせいで `?action=tree` と `?action=file` が同一キーに衝突し、かつ開いた本が全部キャッシュに溜まるため (詳細は「ライブラリ参照機能」)
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
ルートに変更を加えたら docs/webapp/ にも同期が必要。**HTML の中身は完全に同一なので、単純にファイルをコピーすればよい** (差分を再適用する必要はない):
- `<title>` は**ルート側にも ` - id-fa/simple-pdf-viewer-with-screenshot` を付けて意図的に揃えてある**。コピーだけで同期できるようにするためなので、ルートの title からサフィックスを外さないこと
- Google Analytics の gtag ブロックは**リポジトリには含めない**。`.github/workflows/jekyll-gh-pages.yml` が Pages のビルド時に `<head>` へ注入する (既に `googletagmanager.com` / `gtag(` があればスキップするガード付き)。HTML に手で書き足さないこと
- gtag が残っているのは `v1/` / `docs/webapp/v1/` の旧版スナップショットだけ

`library.php` / `library.config*.php` / `tools/` は **docs/webapp/ にコピーしない**。GitHub Pages では PHP が動かず、置いてもソースがそのまま配信されるだけ。置かなければ 404 になり、クライアント側が「未設置」と判定して Library の UI を自動的に隠す。

## 開発規約
- Vanilla JS のみ、フレームワーク不使用
- HTML ファイルは単一ファイルを維持 (外部 JS/CSS に分割しない)
- ライブラリは `vendor/` に配置 (CDN に依存しない、オフラインで動作)
- ES Modules (`type="module"`) で記述
- Chrome DevTools MCP で動作確認可能
- アセット更新時は `sw.js` の `CACHE_NAME` をインクリメント
