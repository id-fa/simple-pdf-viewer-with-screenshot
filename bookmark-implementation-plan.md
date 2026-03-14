# しおり（ブックマーク）機能 実装計画書

## 概要
サムネイルペインを活用したしおり機能。localStorageにファイル名ハッシュとページ番号リストを保存し、読書位置の記録・復帰を可能にする。

## 対象ファイル
- `pdf-viewer.html`
- `comic-viewer.html`

両ファイルに同等の機能を実装する。以下の説明は共通仕様。

---

## 1. データ設計

### 1.1 localStorage キー構成

| キー | 値 | 説明 |
|------|-----|------|
| `bookmark_enabled` | `"true"` / `"false"` | しおり機能の有効/無効 (デフォルト: `"false"`) |
| `bookmarks` | JSON文字列 | 全書籍のしおりデータ |

### 1.2 ファイル名記録の制御

ソースコード内の変数でファイル名をlocalStorageに記録するか制御する:

```javascript
const BOOKMARK_STORE_FILENAME = false; // true にするとファイル名もlocalStorageに保存する
```

- デフォルト: `false` (ファイル名を保存しない — プライバシー保護)
- `true` にするとエクスポートJSONの可読性が向上する (どのファイルのしおりか識別しやすい)
- ファイル名を保存しなくてもハッシュで書籍を一意に識別できるため、機能には影響しない

### 1.3 `bookmarks` データ構造

```json
{
  "<fileHash>": {
    "fileName": "example.pdf",
    "manual": [3, 15, 42],
    "lastRead": 7,
    "furthest": 25
  },
  "<fileHash2>": { ... }
}
```

- `fileHash` — ファイル名+サイズから生成するハッシュ (後述)
- `fileName` — 表示用のファイル名 (`BOOKMARK_STORE_FILENAME = true` の場合のみ保存、`false` ならこのフィールドは省略)
- `manual` — ユーザーが手動で付けたしおりのページ番号リスト (昇順ソート)
- `lastRead` — 前回最後に開いていたページ (自動しおり1)
- `furthest` — 開いたことのある一番最後のページ (自動しおり2)

### 1.4 ファイルハッシュ生成

```javascript
async function generateFileHash(file) {
  const data = new TextEncoder().encode(file.name + '|' + file.size);
  const hashBuffer = await crypto.subtle.digest('SHA-256', data);
  const hashArray = Array.from(new Uint8Array(hashBuffer));
  return hashArray.map(b => b.toString(16).padStart(2, '0')).join('').substring(0, 16);
}
```

- `file.name` + `file.size` をSHA-256ハッシュし、先頭16文字を使用
- 同名・同サイズのファイルは同一書籍とみなす

---

## 2. UI設計

### 2.1 サムネイルペイン レイアウト (上から順)

```
┌─────────────────────┐
│ ☐ Bookmarks         │ ← 設定欄 (2.2)
├─────────────────────┤
│ ★ p.7 (last read)   │ ← しおり付きページ一覧 (2.3)
│ ★ p.25 (furthest)   │
│ ● p.3               │
│ ● p.15              │
│ ● p.42              │
├─────────────────────┤
│ [Thumb p.1]     [♦] │ ← サムネイル (2.4)
│ [Thumb p.2]         │
│ [Thumb p.3]     [●] │ ← しおり付きにはマーカー表示
│ ...                  │
├─────────────────────┤
│ 管理機能             │ ← 管理セクション (2.5)
│ [Clear this book]    │
│ [Clear all]          │
│ [Export] [Import]    │
└─────────────────────┘
```

### 2.2 設定欄 (サムネイルペイン最上部)

```html
<div class="bookmark-settings">
  <label>
    <input type="checkbox" id="bookmarkEnabled"> Bookmarks
  </label>
</div>
```

- チェックON → `bookmark_enabled = "true"` をlocalStorageに保存
- 以降すべての本で有効になる (グローバル設定)
- チェックOFF時:
  1. 確認ダイアログ: 「しおり機能を無効にします。登録済みのしおりデータを消去しますか？」
  2. 「消去する」→ `bookmarks` キーをlocalStorageから削除
  3. 「保持する」→ `bookmark_enabled` のみ `"false"` に (`bookmarks` データは残す)

### 2.3 しおり付きページ一覧 (設定欄の下)

```html
<div class="bookmark-list" id="bookmarkList">
  <!-- 動的に生成 -->
  <div class="bookmark-entry auto" data-page="7">
    <span class="bookmark-icon auto">◆</span> p.7 <span class="bookmark-label">last read</span>
  </div>
  <div class="bookmark-entry auto" data-page="25">
    <span class="bookmark-icon auto">◆</span> p.25 <span class="bookmark-label">furthest</span>
  </div>
  <div class="bookmark-entry manual" data-page="3">
    <span class="bookmark-icon manual">●</span> p.3
  </div>
</div>
```

- クリックでそのページにジャンプ (`renderView(page)`)
- 自動しおり (auto): 別色アイコン (例: オレンジ系 `◆`)
- 手動しおり (manual): 通常色アイコン (例: 青系 `●`)
- しおりが無い場合は非表示
- しおり機能が無効の場合はセクション全体を非表示

### 2.4 サムネイルのしおりマーカー

各サムネイル `.thumb` の右上にしおりマーカーを配置:

```html
<div class="thumb">
  <canvas>...</canvas>
  <div class="num">3</div>
  <div class="bookmark-marker" data-page="3">●</div>  <!-- 追加 -->
</div>
```

CSS:
```css
.bookmark-marker {
  position: absolute;
  top: 2px;
  right: 4px;
  font-size: 12px;
  cursor: pointer;
  opacity: 0.3;        /* 未設定時は薄く表示 */
  color: #4a9eff;
  transition: opacity 0.15s;
}
.bookmark-marker:hover {
  opacity: 0.7;
}
.bookmark-marker.active {
  opacity: 1.0;        /* 設定済みは常時表示 */
}
.bookmark-marker.auto {
  color: #ff9f43;      /* 自動しおりはオレンジ */
  pointer-events: none; /* 自動しおりはクリック不可 */
}
```

- クリックで手動しおりのトグル (設定/解除)
- 自動しおりのページにもマーカー表示 (オレンジ色、クリック不可)
- しおり機能が無効の場合はマーカー非表示

### 2.5 管理セクション (サムネイルペイン最下部)

```html
<div class="bookmark-management">
  <div class="bookmark-mgmt-title">Bookmark Management</div>
  <button id="clearBookmarksThis">Clear this book</button>
  <button id="clearBookmarksAll">Clear all books</button>
  <div class="bookmark-mgmt-row">
    <button id="exportBookmarks">Export</button>
    <button id="importBookmarks">Import</button>
    <input type="file" id="importBookmarksFile" accept=".json" hidden>
  </div>
</div>
```

- しおり機能が無効の場合は非表示

---

## 3. 機能詳細

### 3.1 自動しおり

#### lastRead (前回最後に開いていたページ)
- **更新タイミング**: `renderView()` 呼び出し時 (ページ切替のたびに更新)
- **動作**: 常に現在表示中のページで上書き
- 読み戻った場合はその位置が保存される

#### furthest (開いたことのある一番最後のページ)
- **更新タイミング**: `renderView()` 呼び出し時
- **動作**: `Math.max(currentFurthest, newPage)` — 既存値より大きい場合のみ更新
- 読み戻りしても値は減らない

#### 自動保存の抑制
- `bookmark_enabled !== "true"` の場合、自動しおりの更新・保存を一切行わない

### 3.2 手動しおり

- サムネイルのマーカークリックでトグル
- しおり一覧からも確認・ジャンプ可能
- `manual` 配列に追加/削除し、即座にlocalStorageへ保存

### 3.3 管理機能

#### Clear this book
- 現在開いている本のしおりデータを削除
- 確認ダイアログなし (単一書籍のため復旧容易)

#### Clear all books
- 確認ダイアログ: 「すべての本のしおりデータを消去しますか？この操作は取り消せません。」
- OK → `bookmarks` キーを空オブジェクトで上書き

#### Export
- `bookmarks` のJSON文字列をファイルダウンロード
- ファイル名: `bookmarks_YYYY-MM-DD.json`

#### Import
- JSONファイルを読み込み
- 既存データとマージ (同一ハッシュは上書き)
- 不正なJSON時はエラー表示

---

## 4. 実装手順

### Phase 1: 基盤 (localStorage + ハッシュ)

1. `generateFileHash(file)` 関数を追加
2. `loadBookmarks()` / `saveBookmarks()` ヘルパー関数を追加
3. `getBookmarkForFile(hash)` / `setBookmarkForFile(hash, data)` を追加
4. ファイル読み込み時にハッシュを生成して変数 `currentFileHash` に保持

### Phase 2: 設定UI

5. サムネイルペインの `renderThumbnails()` を拡張し、最上部に設定チェックボックスを生成
6. チェックボックスのイベントハンドラ (有効/無効切替、確認ダイアログ)
7. 起動時に `bookmark_enabled` を読み込んでチェックボックスに反映

### Phase 3: 自動しおり

8. `renderView()` 内に自動しおり更新ロジックを追加
9. `lastRead` / `furthest` の更新と保存
10. しおり機能無効時のガード条件

### Phase 4: しおり一覧UI

11. サムネイルペイン上部にしおり一覧セクションを生成する `renderBookmarkList()` 関数
12. 一覧アイテムのクリックでページジャンプ
13. `renderView()` 呼び出し後に一覧を更新

### Phase 5: サムネイルマーカー

14. `renderThumbnails()` 内で各サムネイルにマーカー要素を追加
15. マーカークリックで手動しおりトグル
16. 自動しおりのページにはオレンジマーカー表示
17. `updateBookmarkMarkers()` — マーカーの表示状態を更新する関数

### Phase 6: 管理機能

18. サムネイルペイン最下部に管理セクションを生成
19. Clear this book の実装
20. Clear all books の実装 (確認ダイアログ付き)
21. Export の実装 (JSONダウンロード)
22. Import の実装 (JSONアップロード + マージ)

### Phase 7: 統合・調整

23. しおり機能有効/無効の切替時にUI全体を正しく更新
24. ファイル切替時 (新しいPDF/アーカイブ読み込み時) のしおり読み込み
25. 見開き表示時の考慮 (しおりは個別ページ単位で管理)

---

## 5. CSS追加

```css
/* Bookmark Settings */
.bookmark-settings {
  padding: 8px 4px;
  border-bottom: 1px solid #333;
  margin-bottom: 4px;
  font-size: 12px;
}

/* Bookmark List */
.bookmark-list {
  padding: 4px;
  border-bottom: 1px solid #333;
  margin-bottom: 4px;
  max-height: 200px;
  overflow-y: auto;
}
.bookmark-entry {
  padding: 3px 6px;
  cursor: pointer;
  font-size: 11px;
  border-radius: 3px;
}
.bookmark-entry:hover {
  background: rgba(255,255,255,0.1);
}
.bookmark-icon.auto { color: #ff9f43; }
.bookmark-icon.manual { color: #4a9eff; }
.bookmark-label {
  font-size: 10px;
  opacity: 0.6;
}

/* Bookmark Management */
.bookmark-management {
  padding: 8px 4px;
  border-top: 1px solid #333;
  margin-top: 4px;
}
.bookmark-management button {
  width: 100%;
  margin-bottom: 4px;
  font-size: 11px;
  padding: 4px;
}
.bookmark-mgmt-row {
  display: flex;
  gap: 4px;
}
.bookmark-mgmt-row button {
  flex: 1;
}
```

---

## 6. 注意事項

- **サムネイルペインの再構築**: `renderThumbnails()` は現在 `sidebar.innerHTML = ''` で全消去→再生成している。しおりUI (設定欄、一覧、管理セクション) はサムネイルとは別のコンテナに配置し、`renderThumbnails()` で消されないようにする
- **パフォーマンス**: しおりの保存は `renderView()` のたびに発生するが、localStorage書き込みは軽量なので問題ない。ただしデバウンスを検討してもよい
- **comic-viewer.html のファイルハッシュ**: 二重アーカイブの場合は外側+内側のファイル名を結合してハッシュする
- **しおり数の上限**: 特に設けないが、localStorageの5MB制限に注意。しおりデータは極めて軽量なので実用上問題ない
- **両ファイル間のデータ互換**: 同じlocalStorageキー・データ構造を使用するため、同じブラウザで両ビューアを使えばしおりデータは共有される
