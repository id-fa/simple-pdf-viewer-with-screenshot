# Comic Viewer

ブラウザベースの汎用コミックビューア。PDF / CBZ / CBR / CB7 に対応。単一HTMLファイルで完結。

## 対応形式

| 形式 | 拡張子 | ライブラリ |
|------|--------|-----------|
| PDF | `.pdf` | [PDF.js](https://mozilla.github.io/pdf.js/) v4.9.155 |
| CBZ | `.cbz`, `.zip` | [libarchive.js](https://github.com/nicka-begiashvili/libarchivejs) v2.0.2 (WASM) |
| CBR | `.cbr`, `.rar` | libarchive.js v2.0.2 (WASM) |
| CB7 | `.cb7`, `.7z` | libarchive.js v2.0.2 (WASM) |

アーカイブ内の画像ファイル (JPEG, PNG, WebP, GIF, BMP, AVIF, JXL, TIFF) を自動検出して表示します。

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
| 50% ~ 200% / Fit | 表示スケール |
| Thumbs | サムネイルサイドバー表示 |

### キーボードショートカット

| キー | R2L (右綴じ) | L2R (左綴じ) |
|------|-------------|-------------|
| ← | 次ページ | 前ページ |
| → | 前ページ | 次ページ |
| ↑ | 前ページ | 前ページ |
| ↓ | 次ページ | 次ページ |
| Home | 最初のページ | 最初のページ |
| End | 最後のページ | 最後のページ |

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
