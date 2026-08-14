<?php
declare(strict_types=1);

/**
 * library.php — サーバー上の指定フォルダを Comic Viewer / PDF Viewer から参照するための最小 API。
 *
 * エンドポイント (すべて GET):
 *   ?action=ping                  → {ok:true, root:"表示名", count:N}
 *   ?action=tree                  → ルート以下の対象ファイルを一括で返す (数百件想定なのでページングなし)
 *   ?action=file&path=sub/a.cbz   → ファイル本体をストリーム配信 (Range 対応)
 *   ?action=cover&path=sub/a.cbz&w=320
 *                                 → 事前に用意された表紙画像 (a.cbz.coverimage.webp 等) を配信。
 *                                    自動生成はしない。GD があれば w に合わせて縮小する
 *
 * 設定は同じディレクトリの library.config.php (library.config.example.php をコピーして作成)。
 *
 * セキュリティ:
 *   - 'root' は DOCUMENT_ROOT の外に置いてよい (むしろ推奨)。PHP が読めれば場所は問わない。
 *     外に置けば直リンク用の URL が存在しなくなるので、直接ダウンロードは原理的に不可能になる。
 *   - パス検証は realpath 後の前方一致で行う。文字列の '..' 除去だけではシンボリックリンクによる
 *     root 外への脱出を防げないため。
 *   - 拡張子ホワイトリスト外・ドットで始まる名前は tree にも載せず file でも配信しない。
 *   - アクセス制限が必要なら設定の 'auth' に ID / パスワードを書く (下記 lib_require_auth)。
 *     サーバー設定が要らないうえ、認証の影響範囲がこのファイルに閉じるので Service Worker の
 *     プリキャッシュを巻き込む事故が起きない。.htaccess で掛けたい場合は library.htaccess.example
 *     を参照 (その場合も **この library.php にのみ** 掛けること。アプリ全体に掛けると
 *     Service Worker のプリキャッシュが 401 で静かに失敗し、オフライン動作が壊れる)。
 */

// ---------------------------------------------------------------- helpers

function lib_json($data, int $status = 200): void
{
    http_response_code($status);
    header('Content-Type: application/json; charset=utf-8');
    header('Cache-Control: no-store');
    header('X-Content-Type-Options: nosniff');
    echo json_encode($data, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    exit;
}

function lib_fail(int $status, string $msg): void
{
    lib_json(['error' => $msg], $status);
}

/**
 * Basic 認証で使う ID / パスワードを取り出す。
 *
 * PHP_AUTH_USER / PHP_AUTH_PW は SAPI 依存で、埋まらない環境がある:
 *   - Apache + mod_php / nginx + php-fpm / ビルトインサーバー → 埋まる
 *   - **Apache + CGI / FastCGI / php-fpm** → Apache が CGI 環境から Authorization を
 *     意図的に落とすので埋まらない。そのままだと正しいパスワードを入れても通らず、
 *     認証ダイアログが無限に出続ける。
 * そのため Authorization ヘッダーからの復号にフォールバックする。
 * (Apache 2.4.13+ なら `CGIPassAuth On`、それ以前なら .htaccess の
 *  `SetEnvIf Authorization "(.*)" HTTP_AUTHORIZATION=$1` でヘッダーを通す必要がある)
 */
function lib_basic_credentials(): array
{
    if (isset($_SERVER['PHP_AUTH_USER'], $_SERVER['PHP_AUTH_PW'])) {
        return [(string)$_SERVER['PHP_AUTH_USER'], (string)$_SERVER['PHP_AUTH_PW']];
    }
    $header = '';
    foreach (['HTTP_AUTHORIZATION', 'REDIRECT_HTTP_AUTHORIZATION'] as $k) {
        if (!empty($_SERVER[$k])) { $header = (string)$_SERVER[$k]; break; }
    }
    if ($header !== '' && stripos($header, 'basic ') === 0) {
        $decoded = base64_decode(substr($header, 6), true);
        if ($decoded !== false && strpos($decoded, ':') !== false) {
            return explode(':', $decoded, 2);
        }
    }
    return ['', ''];
}

/** 設定に 'auth' があるときだけ Basic 認証を要求する */
function lib_require_auth(array $cfg): void
{
    $auth = $cfg['auth'] ?? null;
    if (!is_array($auth) || ($auth['user'] ?? '') === '' || ($auth['pass'] ?? '') === '') return;

    [$user, $pass] = lib_basic_credentials();
    $expected = (string)$auth['pass'];
    // '$2y$' / '$argon2' で始まるならハッシュ (password_hash 済み) とみなす
    $passOk = (strlen($expected) > 3 && $expected[0] === '$')
        ? password_verify($pass, $expected)
        : hash_equals($expected, $pass);          // 平文比較でもタイミング差を出さない
    $userOk = hash_equals((string)$auth['user'], $user);

    if ($userOk && $passOk) return;

    // WWW-Authenticate を送ると PHP が自動でステータスを 401 にする (main/SAPI.c)。
    // 依存したくないので明示的にも設定しておく。
    http_response_code(401);
    header('WWW-Authenticate: Basic realm="' . str_replace('"', '', (string)($auth['realm'] ?? 'Library')) . '"');
    header('Content-Type: application/json; charset=utf-8');
    header('Cache-Control: no-store');
    echo json_encode(
        ['error' => 'ライブラリの閲覧にはログインが必要です / Sign in to browse the library'],
        JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES   // lib_json() と同じエンコード指定に揃える
    );
    exit;
}

/** OS 依存の区切りを '/' に統一し、末尾の '/' を落とす */
function lib_norm_sep(string $p): string
{
    $p = str_replace('\\', '/', $p);
    return ($p !== '/') ? rtrim($p, '/') : $p;
}

/**
 * ファイルシステムのバイト列 → クライアントへ返す UTF-8 (NFC)。
 * Windows の PHP は scandir が ANSI コードページ (日本語環境なら CP932) を返すため、
 * その場合は設定の 'fsEncoding' で変換する。
 */
function lib_to_utf8(string $s, string $fsEncoding): ?string
{
    if ($fsEncoding !== '' && function_exists('mb_convert_encoding')) {
        $conv = @mb_convert_encoding($s, 'UTF-8', $fsEncoding);
        if (is_string($conv) && $conv !== '') $s = $conv;
    }
    if (function_exists('mb_check_encoding') && !mb_check_encoding($s, 'UTF-8')) {
        return null; // 文字化けしたまま返すと検索もしおりハッシュも壊れるので落とす
    }
    if (class_exists('Normalizer')) {
        // macOS 由来の NFD をそのまま返すと、クライアント側の検索・ソートが分解済み文字で崩れる
        $n = \Normalizer::normalize($s, \Normalizer::FORM_C);
        if (is_string($n)) $s = $n;
    }
    return $s;
}

/** クライアントから来た UTF-8 パス → ファイルシステムのバイト列 */
function lib_from_utf8(string $s, string $fsEncoding): string
{
    if ($fsEncoding !== '' && function_exists('mb_convert_encoding')) {
        $conv = @mb_convert_encoding($s, $fsEncoding, 'UTF-8');
        if (is_string($conv) && $conv !== '') return $conv;
    }
    return $s;
}

/**
 * 相対パスを root 配下の実パスに解決する。root の外に出るものは false。
 * realpath はシンボリックリンクも解決するので、リンク経由の脱出もここで塞げる。
 */
function lib_resolve(string $rootReal, string $rel)
{
    if (strpos($rel, "\0") !== false) return false;
    $rel = trim(str_replace('\\', '/', $rel), '/');
    if ($rel === '') return $rootReal;

    $full = realpath($rootReal . '/' . $rel);
    if ($full === false) {
        // macOS (NFD) 等、正規化形が違うと realpath が外れることがあるので分解形で再試行
        if (class_exists('Normalizer')) {
            $alt = \Normalizer::normalize($rel, \Normalizer::FORM_D);
            if (is_string($alt) && $alt !== $rel) $full = realpath($rootReal . '/' . $alt);
        }
        if ($full === false) return false;
    }
    $full = lib_norm_sep($full);
    if ($full === $rootReal) return $full;
    if (strncmp($full, $rootReal . '/', strlen($rootReal) + 1) !== 0) return false;
    return $full;
}

function lib_ext(string $name): string
{
    $pos = strrpos($name, '.');
    return ($pos === false) ? '' : strtolower(substr($name, $pos + 1));
}

/**
 * 表紙画像 (サイドカー) のファイル名を探す。無ければ null。
 *
 * 命名規則は「元のファイル名 + coverSuffix + 画像拡張子」 (例: vol01.cbz.coverimage.webp)。
 * 元のファイル名を丸ごと残すので、vol01.cbz と vol01.pdf が同居しても衝突しない。
 * 自動生成はしない — 用意されている画像だけを使う。
 *
 * $namesLower (小文字名 → 実際の名前) を渡すと、走査済みの一覧から引いて stat を節約する。
 * ネットワークストレージでは stat が高いので tree 側では必ず渡すこと。
 */
function lib_find_cover(string $dir, string $name, array $cfg, ?array $namesLower = null): ?string
{
    foreach ($cfg['coverExts'] as $ext) {
        $cand = $name . $cfg['coverSuffix'] . '.' . $ext;
        if ($namesLower !== null) {
            $key = strtolower($cand);
            if (isset($namesLower[$key])) return $namesLower[$key];
        } elseif (is_file($dir . '/' . $cand)) {
            return $cand;
        }
    }
    return null;
}

/**
 * root 以下を再帰的に走査して対象ファイルを集める。
 * 走査済みの実パスを $seen に記録し、シンボリックリンクのループで無限再帰しないようにする。
 */
function lib_walk(string $dir, string $rel, array $cfg, array &$out, array &$seen, int $depth, array &$stat): void
{
    if ($depth > $cfg['maxDepth'] || count($out) >= $cfg['maxEntries']) return;
    $names = @scandir($dir);
    if ($names === false) { $stat['unreadable']++; return; }

    // 表紙サイドカーの有無を stat 無しで判定するための索引 (小文字名 → 実際の名前)
    $namesLower = [];
    foreach ($names as $n) $namesLower[strtolower($n)] = $n;

    foreach ($names as $name) {
        if ($name === '.' || $name === '..') continue;
        if ($name[0] === '.') continue;                      // ドットファイル/フォルダは対象外
        if (count($out) >= $cfg['maxEntries']) { $stat['truncated'] = true; return; }

        $path = $dir . '/' . $name;
        if (!$cfg['followSymlinks'] && is_link($path)) continue;

        $utf8 = lib_to_utf8($name, $cfg['fsEncoding']);
        if ($utf8 === null) { $stat['badName']++; continue; }
        $childRel = ($rel === '') ? $utf8 : $rel . '/' . $utf8;

        if (is_dir($path)) {
            $real = realpath($path);
            if ($real === false) continue;
            $real = lib_norm_sep($real);
            if (isset($seen[$real])) continue;               // symlink ループ対策
            $seen[$real] = true;
            lib_walk($path, $childRel, $cfg, $out, $seen, $depth + 1, $stat);
        } elseif (is_file($path)) {
            if (!in_array(lib_ext($name), $cfg['exts'], true)) continue;
            $size = @filesize($path);
            $mtime = @filemtime($path);
            $entry = [
                'path'  => $childRel,
                'size'  => ($size === false) ? 0 : $size,
                'mtime' => ($mtime === false) ? 0 : $mtime,
            ];
            // 表紙があるものだけ cover: true を付ける (無いエントリではキーごと省いて JSON を小さく保つ)
            if (lib_find_cover($dir, $name, $cfg, $namesLower) !== null) $entry['cover'] = true;
            $out[] = $entry;
        }
    }
}

function lib_image_mime(string $ext): string
{
    switch ($ext) {
        case 'webp': return 'image/webp';
        case 'avif': return 'image/avif';
        case 'png':  return 'image/png';
        case 'gif':  return 'image/gif';
        default:     return 'image/jpeg';
    }
}

/**
 * 表紙画像を配信する。
 *
 * - $maxW が指定され、GD が使えて、元画像がそれより大きいときだけ縮小して返す。
 *   縮小できない (GD 無し / 未対応形式 / 巨大すぎる) 場合は元画像をそのまま返す。
 * - 本編と違いキャッシュを許可する。表紙は小さく滅多に変わらないので、ブラウザに持たせないと
 *   サムネイル表示のたびに全件を取り直すことになる。Service Worker は library.php を
 *   素通しするので、ここで指定したヘッダーがそのままブラウザキャッシュに効く。
 */
function lib_send_cover(string $full, int $maxW): void
{
    $mtime = @filemtime($full) ?: 0;
    $size  = @filesize($full) ?: 0;
    $etag  = '"' . md5($full . '|' . $mtime . '|' . $size . '|' . $maxW) . '"';

    header('Cache-Control: private, max-age=604800');
    header('ETag: ' . $etag);
    header('X-Content-Type-Options: nosniff');
    if (isset($_SERVER['HTTP_IF_NONE_MATCH']) && trim($_SERVER['HTTP_IF_NONE_MATCH']) === $etag) {
        http_response_code(304);
        exit;
    }

    $ext = lib_ext($full);
    $resized = ($maxW > 0) ? lib_resize_image($full, $maxW) : null;

    while (ob_get_level() > 0) ob_end_clean();
    @ini_set('zlib.output_compression', '0');

    if ($resized !== null) {
        header('Content-Type: ' . $resized['mime']);
        header('Content-Length: ' . strlen($resized['data']));
        echo $resized['data'];
        exit;
    }

    header('Content-Type: ' . lib_image_mime($ext));
    header('Content-Length: ' . $size);
    @readfile($full);
    exit;
}

/**
 * GD で幅 $maxW に収まるよう縮小する。縮小不要 / 不可能なら null (呼び出し側が原本を返す)。
 * 出力は WebP を優先し、使えなければ JPEG。
 */
function lib_resize_image(string $full, int $maxW): ?array
{
    if (!function_exists('imagecreatefromstring') || !function_exists('imagescale')) return null;

    $info = @getimagesize($full);
    if ($info === false) return null;
    [$w, $h] = $info;
    if ($w <= 0 || $h <= 0) return null;
    if ($w <= $maxW) return null;                       // 元がすでに小さい
    if ($w * $h > 40000000) return null;                // 巨大画像はデコードせず原本を返す

    $raw = @file_get_contents($full);
    if ($raw === false) return null;
    $src = @imagecreatefromstring($raw);
    unset($raw);
    if ($src === false) return null;

    $dst = @imagescale($src, $maxW, -1, IMG_BICUBIC);
    imagedestroy($src);
    if ($dst === false) return null;

    ob_start();
    if (function_exists('imagewebp')) {
        $mime = 'image/webp';
        imagewebp($dst, null, 82);
    } else {
        $mime = 'image/jpeg';
        imagejpeg($dst, null, 85);
    }
    $data = ob_get_clean();
    imagedestroy($dst);

    return ($data === false || $data === '') ? null : ['data' => $data, 'mime' => $mime];
}

function lib_mime(string $ext): string
{
    switch ($ext) {
        case 'pdf':  return 'application/pdf';
        case 'epub': return 'application/epub+zip';
        case 'cbz':  return 'application/vnd.comicbook+zip';
        case 'cbr':  return 'application/vnd.comicbook-rar';
        case 'zip':  return 'application/zip';
        case 'rar':  return 'application/vnd.rar';
        case '7z':
        case 'cb7':  return 'application/x-7z-compressed';
        default:     return 'application/octet-stream';
    }
}

/** Range 対応のストリーム配信。大きいアーカイブでも一度にメモリへ載せない。 */
function lib_send_file(string $full, string $name): void
{
    $size = filesize($full);
    if ($size === false) lib_fail(500, 'ファイルサイズを取得できません / Could not determine the file size');

    $start = 0;
    $end   = $size - 1;
    $partial = false;

    if (isset($_SERVER['HTTP_RANGE']) && preg_match('/^bytes=(\d*)-(\d*)$/', trim($_SERVER['HTTP_RANGE']), $m)) {
        if ($m[1] === '' && $m[2] === '') {
            // 無効な Range。全体を返す
        } elseif ($m[1] === '') {
            $start = max(0, $size - (int)$m[2]);              // 末尾 N バイト
        } else {
            $start = (int)$m[1];
            if ($m[2] !== '') $end = min((int)$m[2], $size - 1);
        }
        if ($start > $end || $start >= $size) {
            http_response_code(416);
            header('Content-Range: bytes */' . $size);
            exit;
        }
        $partial = ($start !== 0 || $end !== $size - 1);
    }

    $len = $end - $start + 1;
    $fh = @fopen($full, 'rb');
    if ($fh === false) lib_fail(500, 'ファイルを開けません / Could not open the file');

    // 出力バッファ・圧縮を切る (Content-Length と実バイト数がずれるのを防ぐ)
    while (ob_get_level() > 0) ob_end_clean();
    @ini_set('zlib.output_compression', '0');
    @set_time_limit(0);

    http_response_code($partial ? 206 : 200);
    header('Content-Type: ' . lib_mime(lib_ext($name)));
    header('Content-Length: ' . $len);
    header('Accept-Ranges: bytes');
    if ($partial) header("Content-Range: bytes $start-$end/$size");
    // 数百MBのアーカイブをブラウザキャッシュに溜めても得がないので保存しない
    header('Cache-Control: no-store');
    header('X-Content-Type-Options: nosniff');
    header("Content-Disposition: inline; filename*=UTF-8''" . rawurlencode($name));

    if ($start > 0) fseek($fh, $start);
    $remain = $len;
    while ($remain > 0 && !feof($fh)) {
        $chunk = fread($fh, (int)min(1024 * 512, $remain));
        if ($chunk === false || $chunk === '') break;
        echo $chunk;
        $remain -= strlen($chunk);
        flush();
        if (connection_aborted()) break;
    }
    fclose($fh);
    exit;
}

// ---------------------------------------------------------------- main

$cfgFile = __DIR__ . '/library.config.php';
if (!is_file($cfgFile)) {
    lib_fail(503, 'library.config.php がありません。library.config.example.php をコピーして作成してください。'
        . ' / library.config.php is missing. Copy library.config.example.php to create it.');
}

$user = require $cfgFile;
if (!is_array($user)) lib_fail(500, 'library.config.php は設定の配列を return してください。'
    . ' / library.config.php must return a configuration array.');

$cfg = array_merge([
    'root'           => '',
    'label'          => '',
    'auth'           => null,
    'exts'           => ['pdf', 'cbz', 'cbr', 'cb7', 'epub', 'zip', 'rar', '7z'],
    'coverSuffix'    => '.coverimage',
    'coverExts'      => ['webp', 'avif', 'png', 'jpg', 'jpeg', 'gif'],
    'maxEntries'     => 5000,
    'maxDepth'       => 12,
    'followSymlinks' => false,
    'fsEncoding'     => '',
], $user);

$cfg['exts'] = array_map('strtolower', (array)$cfg['exts']);
$cfg['coverExts'] = array_map('strtolower', (array)$cfg['coverExts']);
$cfg['coverSuffix'] = str_replace(['/', '\\', "\0"], '', (string)$cfg['coverSuffix']);

// 設定の読み込み直後・ファイルに触れる前に認証する
lib_require_auth($cfg);

if ($cfg['root'] === '') lib_fail(500, "library.config.php の 'root' が設定されていません。"
        . " / 'root' is not set in library.config.php.");
$rootReal = realpath($cfg['root']);
if ($rootReal === false || !is_dir($rootReal)) {
    lib_fail(500, "root フォルダが見つかりません / The root folder was not found: " . $cfg['root']);
}
$rootReal = lib_norm_sep($rootReal);

$action = isset($_GET['action']) ? (string)$_GET['action'] : '';

if ($action === 'ping') {
    $label = ($cfg['label'] !== '') ? $cfg['label'] : basename($rootReal);
    lib_json(['ok' => true, 'root' => $label, 'exts' => array_values($cfg['exts'])]);
}

if ($action === 'tree') {
    $out  = [];
    $seen = [$rootReal => true];
    $stat = ['truncated' => false, 'badName' => 0, 'unreadable' => 0];
    lib_walk($rootReal, '', $cfg, $out, $seen, 0, $stat);

    $warnings = [];
    if ($stat['badName'] > 0) {
        $warnings[] = "UTF-8 として解釈できないファイル名を {$stat['badName']} 件除外しました。"
            . "Windows サーバーでは library.config.php の 'fsEncoding' に 'SJIS-win' を設定してください。"
            . " / Skipped {$stat['badName']} file name(s) that are not valid UTF-8."
            . " On Windows servers, set 'fsEncoding' to 'SJIS-win' in library.config.php.";
    }
    if ($stat['unreadable'] > 0) {
        $warnings[] = "読み取れないフォルダが {$stat['unreadable']} 件ありました (権限を確認してください)。"
            . " / {$stat['unreadable']} folder(s) could not be read (check permissions).";
    }
    if ($stat['truncated']) {
        $warnings[] = "件数が上限 ({$cfg['maxEntries']}) に達したため一覧を打ち切りました。"
            . " / The listing was truncated at the limit of {$cfg['maxEntries']} entries.";
    }

    lib_json([
        'root'      => ($cfg['label'] !== '') ? $cfg['label'] : basename($rootReal),
        'generated' => time(),
        'truncated' => $stat['truncated'],
        'warnings'  => $warnings,
        'entries'   => $out,
    ]);
}

/**
 * ?path= を検証して実パスを返す。file と cover で同じ検証を通すための共通処理。
 * 検証に落ちた時点で lib_fail() が exit するので、戻ってきたら安全なパス。
 */
function lib_request_path(string $rootReal, array $cfg): string
{
    $rel = isset($_GET['path']) ? (string)$_GET['path'] : '';
    if ($rel === '') lib_fail(400, 'path が指定されていません / No path was given');

    $full = lib_resolve($rootReal, lib_from_utf8($rel, $cfg['fsEncoding']));
    if ($full === false || !is_file($full)) lib_fail(404, 'ファイルが見つかりません / File not found');

    // ドットで始まる名前は tree に載せていないので file / cover でも配信しない。
    // basename だけでなく途中のフォルダも見る (.hidden/book.cbz を直接要求されるため)。
    foreach (explode('/', substr($full, strlen($rootReal) + 1)) as $seg) {
        if ($seg === '' || $seg[0] === '.') lib_fail(403, 'このパスは配信対象外です / This path is not served');
    }
    if (!in_array(lib_ext(basename($full)), $cfg['exts'], true)) {
        lib_fail(403, 'この形式は配信対象外です / This file type is not served');
    }
    return $full;
}

if ($action === 'file') {
    $full = lib_request_path($rootReal, $cfg);
    $name = basename($full);
    $utf8Name = lib_to_utf8($name, $cfg['fsEncoding']);
    lib_send_file($full, $utf8Name === null ? 'download' : $utf8Name);
}

if ($action === 'cover') {
    // path は「本のパス」を受け取り、表紙のファイル名はサーバー側で導出する。
    // クライアントから画像パスを受け取らないので、表紙経由で任意ファイルを読ませる余地が無い。
    $full = lib_request_path($rootReal, $cfg);
    $cover = lib_find_cover(dirname($full), basename($full), $cfg);
    if ($cover === null) lib_fail(404, '表紙画像がありません / No cover image for this file');

    $maxW = isset($_GET['w']) ? (int)$_GET['w'] : 0;
    $maxW = ($maxW > 0) ? max(64, min(2000, $maxW)) : 0;   // 極端な値でメモリを食わせない
    lib_send_cover(dirname($full) . '/' . $cover, $maxW);
}

lib_fail(400, '不明な action です / Unknown action (ping / tree / file / cover)');
