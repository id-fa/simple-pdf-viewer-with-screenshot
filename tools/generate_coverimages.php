<?php
declare(strict_types=1);

/**
 * generate_coverimages.php — library.php 用の表紙画像 (サイドカー) を一括生成する CLI ツール。
 *
 * library.php は「用意された表紙画像を配信するだけ」で自動生成はしない。このスクリプトが
 * その表紙を作る。命名規則は library.php とまったく同じ:
 *
 *     Manga/vol01.cbz                    ← 本
 *     Manga/vol01.cbz.coverimage.webp    ← その表紙 (元のファイル名を丸ごと残す)
 *
 * 表紙の決め方:
 *   PDF   … 1 ページ目をレンダリング
 *   EPUB  … comic-viewer.html の EPUB 構造解析を移植 (container.xml → OPF → spine →
 *           XHTML の <img>/<svg><image>/背景 url()) して「読み順の 1 枚目」を採用。
 *           spine から取れなければ manifest の image/*、それも無ければファイル名順。
 *   書庫  … ファイル名順 (既定 Lexical / 設定で Natural) の 1 ファイル目
 *
 * 使い方:
 *     php tools/generate_coverimages.php               # library.config.php の root を処理
 *     php tools/generate_coverimages.php --check       # 使えるバックエンドを確認するだけ
 *     php tools/generate_coverimages.php --dry-run -v  # 何が作られるか確認
 *     php tools/generate_coverimages.php --mtime       # 表紙の更新日時を元ファイルに揃える
 *     php tools/generate_coverimages.php --help
 *
 * 依存 (あるものを自動で使う。無ければその形式だけスキップする):
 *   - ZipArchive       … cbz / zip / epub (PHP 標準。ほぼ常に有効)
 *   - Imagick または GD … 画像のデコード・縮小・エンコード
 *   - PDF レンダラ      … Imagick(+Ghostscript) / pdftoppm / mutool / magick / gs のいずれか
 *   - 7z / unrar       … cbr / rar / cb7 / 7z (外部コマンド)
 */

// ---------------------------------------------------------------- 起動ガード

if (PHP_SAPI !== 'cli') {
    // Web から叩かれても何もしない。tools/ をドキュメントルート下に置いてしまった場合の保険。
    http_response_code(403);
    header('Content-Type: text/plain; charset=utf-8');
    echo "このスクリプトは CLI 専用です / This script is CLI only.\n";
    exit(1);
}
if (PHP_VERSION_ID < 70400) {
    fwrite(STDERR, "PHP 7.4 以上が必要です / PHP 7.4 or later is required.\n");
    exit(1);
}

@set_time_limit(0);
@ini_set('memory_limit', '512M');

// ---------------------------------------------------------------- 既定値

/**
 * このツール固有の設定。library.config.php に 'coverTool' => [...] を書けば上書きでき、
 * さらにコマンドラインオプションが最優先になる (library.config.example.php にサンプルあり)。
 */
const COVER_DEFAULTS = [
    // 出力形式と品質
    'format'          => 'webp',   // webp | jpeg | png
    'quality'         => 82,       // webp / jpeg の品質 (1-100)

    // 最大解像度。これを超える画像だけ縮小する (拡大はしない)。0 で無制限
    'maxWidth'        => 1200,
    'maxHeight'       => 1600,

    // 書庫内のファイル名の並び順。comic-viewer.html の Sort と同じ意味
    'sort'            => 'lexical', // lexical | natural

    // EPUB の表紙をどこから取るか
    //   spine    … 読み順の 1 枚目 (= ビューアで最初に表示されるページ)
    //   metadata … OPF の properties="cover-image" / <meta name="cover"> を優先し、
    //              取れなければ spine にフォールバック
    'epubCover'       => 'spine',

    // 表紙の更新日時を抽出元ファイル (PDF/EPUB/書庫そのもの) に合わせる
    'matchMtime'      => false,

    // 縮小が不要かつ元画像の形式が coverExts に含まれるなら、再エンコードせずそのまま書き出す。
    // false なら常に 'format' に正規化する (拡張子が揃うので既定はこちら)
    'passthrough'     => false,

    // 先頭候補がデコードできなかったときに何枚目まで試すか
    'maxCandidates'   => 5,

    // PDF のレンダリング解像度 (dpi)。大きいほど綺麗だが遅い。縮小は後段で行う
    'pdfDpi'          => 150,
    'pdfEngine'       => 'auto',   // auto | imagick | pdftoppm | mutool | magick | gs
    'imageBackend'    => 'auto',   // auto | imagick | gd

    // 外部コマンドのパス (PATH に無ければ絶対パスを書く)
    'commands'        => [
        'pdftoppm' => 'pdftoppm',
        'mutool'   => 'mutool',
        'magick'   => 'magick',
        'gs'       => 'gs',
        '7z'       => '7z',
        'unrar'    => 'unrar',
    ],

    // コンソール出力を変換する文字コード。Windows のコマンドプロンプトで
    // 日本語ファイル名が化けるなら 'SJIS-win' を指定する ('' なら変換しない)
    'consoleEncoding' => '',
];

/** 書庫内で「ページ画像」とみなす拡張子 (comic-viewer.html の IMAGE_EXTS と同じ) */
const COVER_IMAGE_RE = '/\.(jpe?g|png|webp|gif|bmp|avif|jxl|tiff?|heic|heif)$/i';

const XLINK_NS = 'http://www.w3.org/1999/xlink';

// ---------------------------------------------------------------- 出力ヘルパー

$GLOBALS['cov_console_enc'] = '';
$GLOBALS['cov_quiet']       = false;
$GLOBALS['cov_verbose']     = false;

/** コンソールへ 1 行出す (必要なら文字コードを変換する) */
function cov_out(string $s, bool $stderr = false): void
{
    $enc = $GLOBALS['cov_console_enc'];
    if ($enc !== '' && function_exists('mb_convert_encoding')) {
        $conv = @mb_convert_encoding($s, $enc, 'UTF-8');
        if (is_string($conv) && $conv !== '') $s = $conv;
    }
    // `| head` 等でパイプを閉じられたときに Notice を出さない
    @fwrite($stderr ? STDERR : STDOUT, $s . "\n");
}

function cov_info(string $s): void { if (!$GLOBALS['cov_quiet']) cov_out($s); }
function cov_verbose(string $s): void { if ($GLOBALS['cov_verbose'] && !$GLOBALS['cov_quiet']) cov_out($s); }
function cov_warn(string $s): void { cov_out($s, true); }

function cov_die(string $s): void
{
    cov_warn('エラー / Error: ' . $s);
    exit(1);
}

function cov_human_size(int $n): string
{
    if ($n >= 1048576) return sprintf('%.1fMB', $n / 1048576);
    if ($n >= 1024) return sprintf('%.0fKB', $n / 1024);
    return $n . 'B';
}

// ---------------------------------------------------------------- パス / エンコーディング
// library.php と同じ規則で扱う (表示用に UTF-8 化するだけで、FS 操作は生バイト列のまま)

function cov_norm_sep(string $p): string
{
    $p = str_replace('\\', '/', $p);
    return ($p !== '/') ? rtrim($p, '/') : $p;
}

function cov_is_utf8(string $s): bool
{
    return !function_exists('mb_check_encoding') || mb_check_encoding($s, 'UTF-8');
}

/** ファイルシステムのバイト列 → 表示用 UTF-8 (すでに UTF-8 なら変換しない) */
function cov_to_utf8(string $s, string $fsEncoding): string
{
    if ($fsEncoding !== '' && !cov_is_utf8($s) && function_exists('mb_convert_encoding')) {
        $conv = @mb_convert_encoding($s, 'UTF-8', $fsEncoding);
        if (is_string($conv) && $conv !== '') return $conv;
    }
    return $s;
}

function cov_ext(string $name): string
{
    $pos = strrpos($name, '.');
    return ($pos === false) ? '' : strtolower(substr($name, $pos + 1));
}

/** '..' / '.' / 先頭スラッシュを落とす (comic-viewer.html の sanitizePath と同じ) */
function cov_sanitize_path(string $name): string
{
    $parts = [];
    foreach (explode('/', str_replace('\\', '/', $name)) as $seg) {
        if ($seg === '' || $seg === '.' || $seg === '..') continue;
        $parts[] = $seg;
    }
    return implode('/', $parts);
}

/** 書庫エントリ名 ⇄ OPF/XHTML の href を突き合わせるための正規化キー */
function cov_norm_key(string $p): string
{
    $s = rawurldecode($p);
    return strtolower(cov_sanitize_path($s));
}

/** href を基準ディレクトリから解決して正規化キーにする (fragment 除去 + ../ 解決) */
function cov_resolve_href(string $baseDir, string $href): string
{
    $h = (string)preg_replace('/[#?].*$/s', '', str_replace('\\', '/', $href));
    $h = rawurldecode($h);
    if ($h === '') return '';
    $parts = ($h[0] === '/' || $baseDir === '') ? [] : explode('/', $baseDir);
    foreach (explode('/', $h) as $seg) {
        if ($seg === '' || $seg === '.') continue;
        if ($seg === '..') array_pop($parts);
        else $parts[] = $seg;
    }
    return strtolower(implode('/', $parts));
}

function cov_dir_of(string $path): string
{
    $i = strrpos($path, '/');
    return ($i === false) ? '' : substr($path, 0, $i);
}

/** comic-viewer.html の naturalCompare 相当 (数字部分を数値として比較) */
function cov_natural_compare(string $a, string $b): int
{
    preg_match_all('/(\d+)|(\D+)/', $a, $ma);
    preg_match_all('/(\d+)|(\D+)/', $b, $mb);
    $ap = $ma[0]; $bp = $mb[0];
    $n = max(count($ap), count($bp));
    for ($i = 0; $i < $n; $i++) {
        if (!isset($ap[$i])) return -1;
        if (!isset($bp[$i])) return 1;
        if (ctype_digit($ap[$i]) && ctype_digit($bp[$i])) {
            $d = (int)$ap[$i] <=> (int)$bp[$i];
            if ($d !== 0) return $d;
        } else {
            $d = strcmp($ap[$i], $bp[$i]);
            if ($d !== 0) return $d;
        }
    }
    return 0;
}

function cov_sort_names(array $names, string $mode): array
{
    if ($mode === 'natural') {
        usort($names, 'cov_natural_compare');
    } else {
        sort($names, SORT_STRING);   // Lexical = 文字コード順 (comic-viewer.html と同じ)
    }
    return $names;
}

// ---------------------------------------------------------------- 外部コマンド

/**
 * 外部コマンドを実行して stdout をバイナリのまま受け取る。
 * proc_open の配列形式を使うのでシェルを経由せず、ファイル名の空白・'%'・引用符でも壊れない。
 */
function cov_run(array $cmd, int $timeoutSec = 120): array
{
    $desc = [0 => ['pipe', 'r'], 1 => ['pipe', 'w'], 2 => ['pipe', 'w']];
    $proc = @proc_open($cmd, $desc, $pipes);
    if (!is_resource($proc)) return ['code' => -1, 'out' => '', 'err' => 'コマンドを起動できません'];

    fclose($pipes[0]);
    stream_set_blocking($pipes[1], false);
    stream_set_blocking($pipes[2], false);

    $out = ''; $err = '';
    $deadline = microtime(true) + $timeoutSec;
    while (true) {
        $r = [$pipes[1], $pipes[2]];
        $w = $x = null;
        if (@stream_select($r, $w, $x, 0, 200000) > 0) {
            foreach ($r as $fh) {
                $chunk = fread($fh, 65536);
                if ($chunk === false || $chunk === '') continue;
                if ($fh === $pipes[1]) $out .= $chunk; else $err .= $chunk;
            }
        }
        $st = proc_get_status($proc);
        if (!$st['running']) {
            // 残りを吸い出す
            foreach ([1, 2] as $i) {
                while (($chunk = fread($pipes[$i], 65536)) !== false && $chunk !== '') {
                    if ($i === 1) $out .= $chunk; else $err .= $chunk;
                }
            }
            fclose($pipes[1]); fclose($pipes[2]);
            proc_close($proc);
            return ['code' => (int)$st['exitcode'], 'out' => $out, 'err' => $err];
        }
        if (microtime(true) > $deadline) {
            proc_terminate($proc);
            fclose($pipes[1]); fclose($pipes[2]);
            proc_close($proc);
            return ['code' => -1, 'out' => $out, 'err' => 'タイムアウト (' . $timeoutSec . 's)'];
        }
    }
}

/**
 * コマンドが実行できるか確かめる。結果はキャッシュする (1 ファイルごとに探し直さない)。
 * Windows では PATH に無いことが多いので、よくあるインストール先も試す。
 */
function cov_which(string $key, array $cfg): ?string
{
    static $cache = [];
    if (array_key_exists($key, $cache)) return $cache[$key];

    $cands = [$cfg['commands'][$key] ?? $key];
    if (DIRECTORY_SEPARATOR === '\\') {
        $win = [
            '7z'       => ['C:\\Program Files\\7-Zip\\7z.exe', 'C:\\Program Files (x86)\\7-Zip\\7z.exe'],
            'unrar'    => ['C:\\Program Files\\WinRAR\\UnRAR.exe', 'C:\\Program Files (x86)\\UnRAR\\UnRAR.exe'],
            'pdftoppm' => ['C:\\Program Files\\poppler\\bin\\pdftoppm.exe'],
            'gs'       => ['C:\\Program Files\\gs\\bin\\gswin64c.exe'],
        ];
        foreach ($win[$key] ?? [] as $p) $cands[] = $p;
    }

    foreach ($cands as $bin) {
        if ($bin === '') continue;
        // 7z / unrar は引数無しでも usage を吐いて終了する。gs だけ --version でないと対話に入る
        if ($key === '7z' || $key === 'unrar') $probe = [$bin];
        elseif ($key === 'gs') $probe = [$bin, '--version'];
        else $probe = [$bin, '-v'];
        $r = cov_run($probe, 20);
        if ($r['code'] !== -1) { $cache[$key] = $bin; return $bin; }
    }
    $cache[$key] = null;
    return null;
}

// ---------------------------------------------------------------- 書庫アクセス
// ZipArchive (zip 系) / 7z / unrar を順に試し、最初に一覧が取れたものを使う。
// 拡張子が偽装されていても (cbz の中身が rar 等) フォールバックで拾える。

function cov_archive_open(string $path, string $ext, array $cfg): ?array
{
    $zipLike = in_array($ext, ['zip', 'cbz', 'epub'], true);
    $order = $zipLike ? ['zip', '7z'] : (in_array($ext, ['rar', 'cbr'], true) ? ['7z', 'unrar', 'zip'] : ['7z', 'zip']);

    foreach ($order as $kind) {
        if ($kind === 'zip') {
            if (!class_exists('ZipArchive')) continue;
            $zip = new ZipArchive();
            if ($zip->open($path) !== true) continue;
            $names = [];
            for ($i = 0; $i < $zip->numFiles; $i++) {
                $st = $zip->statIndex($i);
                if ($st === false) continue;
                $n = $st['name'];
                if ($n === '' || substr($n, -1) === '/') continue;   // ディレクトリ
                $names[] = $n;
            }
            if (!$names) { $zip->close(); continue; }
            return ['kind' => 'zip', 'zip' => $zip, 'names' => $names, 'path' => $path];
        }

        if ($kind === '7z') {
            $bin = cov_which('7z', $cfg);
            if ($bin === null) continue;
            $r = cov_run([$bin, 'l', '-ba', '-slt', '-sccUTF-8', '-p', '--', $path], 180);
            if ($r['code'] !== 0 && $r['out'] === '') continue;
            $names = cov_parse_7z_list($r['out']);
            if (!$names) continue;
            return ['kind' => '7z', 'bin' => $bin, 'names' => $names, 'path' => $path];
        }

        if ($kind === 'unrar') {
            $bin = cov_which('unrar', $cfg);
            if ($bin === null) continue;
            $r = cov_run([$bin, 'lb', '-p-', '--', $path], 180);
            if ($r['code'] !== 0 && $r['out'] === '') continue;
            $names = [];
            foreach (preg_split('/\r\n|\n|\r/', $r['out']) as $line) {
                $line = str_replace('\\', '/', trim($line));
                if ($line !== '' && substr($line, -1) !== '/') $names[] = $line;
            }
            if (!$names) continue;
            return ['kind' => 'unrar', 'bin' => $bin, 'names' => $names, 'path' => $path];
        }
    }
    return null;
}

/** `7z l -ba -slt` の出力から実ファイルのパスだけ取り出す */
function cov_parse_7z_list(string $out): array
{
    $names = [];
    $cur = null; $isDir = false; $hasAttr = false;
    foreach (preg_split('/\r\n|\n|\r/', $out) as $line) {
        if (trim($line) === '') {
            if ($cur !== null && !$isDir && $hasAttr) $names[] = $cur;
            $cur = null; $isDir = false; $hasAttr = false;
            continue;
        }
        $p = strpos($line, ' = ');
        if ($p === false) continue;
        $key = substr($line, 0, $p);
        $val = substr($line, $p + 3);
        if ($key === 'Path') { $cur = str_replace('\\', '/', $val); }
        elseif ($key === 'Attributes') { $hasAttr = true; if (strpos($val, 'D') !== false) $isDir = true; }
        elseif ($key === 'Folder') { $hasAttr = true; if ($val === '+') $isDir = true; }
    }
    if ($cur !== null && !$isDir && $hasAttr) $names[] = $cur;
    return $names;
}

/** 書庫から 1 エントリを読み出す。失敗したら null */
function cov_archive_read(array $ar, string $name): ?string
{
    if ($ar['kind'] === 'zip') {
        $data = @$ar['zip']->getFromName($name);
        if ($data === false) {
            $idx = $ar['zip']->locateName($name, ZipArchive::FL_NOCASE);
            if ($idx !== false) $data = @$ar['zip']->getFromIndex($idx);
        }
        return ($data === false || $data === null) ? null : $data;
    }
    if ($ar['kind'] === '7z') {
        $r = cov_run([$ar['bin'], 'x', '-so', '-y', '-p', '--', $ar['path'], $name], 180);
        return ($r['out'] === '') ? null : $r['out'];
    }
    if ($ar['kind'] === 'unrar') {
        $r = cov_run([$ar['bin'], 'p', '-inul', '-y', '-p-', '--', $ar['path'], $name], 180);
        return ($r['out'] === '') ? null : $r['out'];
    }
    return null;
}

function cov_archive_close(array $ar): void
{
    if ($ar['kind'] === 'zip') @$ar['zip']->close();
}

/** macOS のリソースフォーク等、ページ画像ではないものを落とす */
function cov_is_page_image(string $name): bool
{
    if (!preg_match(COVER_IMAGE_RE, $name)) return false;
    if (stripos($name, '__MACOSX/') !== false) return false;
    $base = basename($name);
    if (strncmp($base, '._', 2) === 0) return false;
    if (strncmp($base, '.', 1) === 0) return false;
    return true;
}

// ---------------------------------------------------------------- EPUB 構造解析
// comic-viewer.html の analyzeEpub() の移植。ページ順の組み立てまではせず、
// 「読み順の先頭画像」を数枚だけ拾ったら打ち切る (表紙が欲しいだけなので全 spine は読まない)。

/** XML としてパースし、壊れていたら HTML パーサで拾い直す (epubParse 相当) */
function cov_parse_doc(string $text): ?DOMDocument
{
    $prev = libxml_use_internal_errors(true);
    $doc = new DOMDocument();
    $ok = @$doc->loadXML($text, LIBXML_NONET | LIBXML_NOWARNING | LIBXML_NOERROR);
    if (!$ok) {
        $doc = new DOMDocument();
        $head = '<meta http-equiv="Content-Type" content="text/html; charset=utf-8">';
        $ok = @$doc->loadHTML($head . $text, LIBXML_NONET | LIBXML_NOWARNING | LIBXML_NOERROR);
    }
    libxml_clear_errors();
    libxml_use_internal_errors($prev);
    return $ok ? $doc : null;
}

/** local-name() で引くので名前空間の有無に左右されない (JS の querySelector と同じ感覚) */
function cov_xpath_all(DOMDocument $doc, string $expr): array
{
    $xp = new DOMXPath($doc);
    $nodes = @$xp->query($expr);
    return ($nodes === false) ? [] : iterator_to_array($nodes);
}

function cov_attr(DOMElement $el, string $name): string
{
    return $el->hasAttribute($name) ? $el->getAttribute($name) : '';
}

/** XHTML / SVG 内の画像参照を文書順に返す (epubExtractDocImages 相当) */
function cov_epub_doc_images(string $text, string $docPath): array
{
    $doc = cov_parse_doc($text);
    if ($doc === null) return [];
    $dir = cov_dir_of($docPath);
    $out = [];
    $push = function (string $h) use ($dir, &$out): void {
        if ($h === '' || preg_match('/^(data:|https?:|blob:|#)/i', $h)) return;
        $p = cov_resolve_href($dir, $h);
        if ($p !== '') $out[] = $p;
    };
    foreach (cov_xpath_all($doc, '//*[local-name()="img" or local-name()="image" or @style]') as $el) {
        if (!($el instanceof DOMElement)) continue;
        $href = cov_attr($el, 'src');
        if ($href === '') $href = $el->getAttributeNS(XLINK_NS, 'href');
        if ($href === '') $href = cov_attr($el, 'xlink:href');
        if ($href === '') $href = cov_attr($el, 'href');
        if ($href !== '') { $push($href); continue; }
        // 固定レイアウト EPUB は背景画像で組まれていることがある
        if (preg_match('/url\(\s*[\'"]?([^\'")]+)/i', cov_attr($el, 'style'), $m)) $push($m[1]);
    }
    return $out;
}

/**
 * EPUB の読み順から表紙候補 (書庫内の実エントリ名) を最大 $limit 件返す。
 * 構造が読めなければ空配列 (呼び出し側がファイル名順にフォールバックする)。
 */
function cov_epub_candidates(array $ar, array $imageNames, array $cfg, int $limit, ?string &$note): array
{
    $note = null;

    // 正規化キー → 実エントリ名
    $byKey = [];
    foreach ($ar['names'] as $n) $byKey[cov_norm_key($n)] = $n;
    $imgKeys = [];
    foreach ($imageNames as $n) $imgKeys[cov_norm_key($n)] = $n;

    $readByKey = function (string $key) use ($ar, $byKey): ?string {
        if (!isset($byKey[$key])) return null;
        return cov_archive_read($ar, $byKey[$key]);
    };

    // --- OPF を特定 ---
    $opfPath = '';
    if (isset($byKey['meta-inf/container.xml'])) {
        $cxml = $readByKey('meta-inf/container.xml');
        if ($cxml !== null) {
            $cdoc = cov_parse_doc($cxml);
            if ($cdoc !== null) {
                foreach (cov_xpath_all($cdoc, '//*[local-name()="rootfile"][@full-path]') as $rf) {
                    $opfPath = cov_resolve_href('', cov_attr($rf, 'full-path'));
                    break;
                }
            }
        }
    }
    if ($opfPath === '' || !isset($byKey[$opfPath])) {
        $opfPath = '';
        foreach ($ar['names'] as $n) {
            if (preg_match('/\.opf$/i', $n)) { $opfPath = cov_norm_key($n); break; }
        }
    }
    if ($opfPath === '' || !isset($byKey[$opfPath])) { $note = 'OPF が見つかりません'; return []; }

    $opfText = $readByKey($opfPath);
    if ($opfText === null) { $note = 'OPF を読めません'; return []; }
    $opf = cov_parse_doc($opfText);
    if ($opf === null) { $note = 'OPF をパースできません'; return []; }
    $opfDir = cov_dir_of($opfPath);

    // --- manifest (記述順) ---
    $byId = []; $manifest = [];
    foreach (cov_xpath_all($opf, '//*[local-name()="manifest"]//*[local-name()="item"][@href]') as $el) {
        if (!($el instanceof DOMElement)) continue;
        $info = [
            'id'    => cov_attr($el, 'id'),
            'path'  => cov_resolve_href($opfDir, cov_attr($el, 'href')),
            'type'  => strtolower(cov_attr($el, 'media-type')),
            'props' => cov_attr($el, 'properties'),
        ];
        if ($info['path'] === '') continue;
        if ($info['id'] !== '') $byId[$info['id']] = $info;
        $manifest[] = $info;
    }
    if (!$manifest) { $note = 'OPF に manifest がありません'; return []; }

    $out = []; $seen = [];
    $add = function (string $key) use (&$out, &$seen, $imgKeys): bool {
        if ($key === '' || isset($seen[$key]) || !isset($imgKeys[$key])) return false;
        $seen[$key] = true;
        $out[] = $imgKeys[$key];
        return true;
    };

    // --- metadata 優先モード: properties="cover-image" / <meta name="cover"> ---
    if (($cfg['epubCover'] ?? 'spine') === 'metadata') {
        foreach ($manifest as $it) {
            if (preg_match('/(^|\s)cover-image(\s|$)/', $it['props'])) { $add($it['path']); break; }
        }
        if (!$out) {
            foreach (cov_xpath_all($opf, '//*[local-name()="meta"][@name="cover"][@content]') as $m) {
                if (!($m instanceof DOMElement)) continue;
                $ref = $byId[cov_attr($m, 'content')] ?? null;
                if ($ref !== null && $add($ref['path'])) break;
            }
        }
    }

    // --- spine を読み順に辿る ---
    $spine = [];
    foreach (cov_xpath_all($opf, '//*[local-name()="spine"]//*[local-name()="itemref"][@idref]') as $el) {
        if (!($el instanceof DOMElement)) continue;
        $it = $byId[cov_attr($el, 'idref')] ?? null;
        if ($it !== null) $spine[] = $it;
    }

    foreach ($spine as $item) {
        if (count($out) >= $limit) break;
        if (strncmp($item['type'], 'image/', 6) === 0) {   // spine に画像が直接並ぶ EPUB (稀)
            $add($item['path']);
            continue;
        }
        if (!isset($byKey[$item['path']])) continue;
        $text = $readByKey($item['path']);
        if ($text === null) continue;
        foreach (cov_epub_doc_images($text, $item['path']) as $p) {
            $add($p);
            if (count($out) >= $limit) break;
        }
    }

    // --- spine から取れなければ manifest の image/* を記述順に ---
    if (!$out) {
        foreach ($manifest as $it) {
            if (strncmp($it['type'], 'image/', 6) !== 0) continue;
            $add($it['path']);
            if (count($out) >= $limit) break;
        }
        if ($out) $note = 'spine から画像を取れないため manifest 順を使用';
    }
    if (!$out) $note = '読み順から画像を特定できません';
    return $out;
}

// ---------------------------------------------------------------- PDF レンダリング

/** PDF の 1 ページ目を画像バイト列にする。使えたエンジンは記憶して次回から直行する */
function cov_pdf_first_page(string $path, array $cfg, ?string &$err): ?string
{
    static $picked = null;
    $err = null;
    $dpi = max(24, (int)$cfg['pdfDpi']);

    $engines = ($cfg['pdfEngine'] !== 'auto')
        ? [$cfg['pdfEngine']]
        : ($picked !== null ? [$picked] : ['imagick', 'pdftoppm', 'mutool', 'magick', 'gs']);

    $errs = [];
    foreach ($engines as $eng) {
        $bytes = null;
        if ($eng === 'imagick') {
            if (!class_exists('Imagick')) { $errs[] = 'imagick: 拡張なし'; continue; }
            try {
                $im = new Imagick();
                $im->setResolution($dpi, $dpi);
                $im->readImage($path . '[0]');            // Ghostscript / policy.xml が要る
                $im->setImageBackgroundColor('white');
                $flat = $im->flattenImages();
                $im->clear();
                $flat->setImageFormat('png');
                $bytes = $flat->getImageBlob();
                $flat->clear();
            } catch (Throwable $e) {
                $errs[] = 'imagick: ' . $e->getMessage();
                $bytes = null;
            }
        } else {
            $bin = cov_which($eng, $cfg);
            if ($bin === null) { $errs[] = $eng . ': 見つかりません'; continue; }
            switch ($eng) {
                case 'pdftoppm':
                    $cmd = [$bin, '-png', '-r', (string)$dpi, '-f', '1', '-l', '1', '-singlefile', $path, '-'];
                    break;
                case 'mutool':
                    $cmd = [$bin, 'draw', '-F', 'png', '-o', '-', '-r', (string)$dpi, $path, '1'];
                    break;
                case 'magick':
                    $cmd = [$bin, '-density', (string)$dpi, $path . '[0]', '-background', 'white', '-flatten', 'png:-'];
                    break;
                case 'gs':
                    $cmd = [$bin, '-q', '-dNOPAUSE', '-dBATCH', '-dSAFER', '-sDEVICE=png16m',
                            '-dFirstPage=1', '-dLastPage=1', '-r' . $dpi, '-sOutputFile=-', $path];
                    break;
                default:
                    $errs[] = $eng . ': 不明なエンジン';
                    continue 2;
            }
            $r = cov_run($cmd, 300);
            if ($r['out'] !== '') $bytes = $r['out'];
            else $errs[] = $eng . ': ' . trim(substr($r['err'], 0, 200));
        }

        if ($bytes !== null && $bytes !== '' && cov_probe($bytes) !== null) {
            if ($cfg['pdfEngine'] === 'auto') $picked = $eng;
            return $bytes;
        }
        if ($bytes !== null && $bytes !== '') $errs[] = $eng . ': 出力を画像として認識できません';
    }
    $err = implode(' / ', $errs);
    return null;
}

// ---------------------------------------------------------------- 画像処理

/** 画像バイト列のサイズを調べる。認識できなければ null */
function cov_probe(string $bytes): ?array
{
    $info = @getimagesizefromstring($bytes);
    if ($info !== false && !empty($info[0]) && !empty($info[1])) {
        return ['w' => (int)$info[0], 'h' => (int)$info[1]];
    }
    if (class_exists('Imagick')) {
        try {
            $im = new Imagick();
            $im->readImageBlob($bytes);
            $g = $im->getImageGeometry();
            $im->clear();
            if (!empty($g['width']) && !empty($g['height'])) return ['w' => (int)$g['width'], 'h' => (int)$g['height']];
        } catch (Throwable $e) { /* 未対応形式 */ }
    }
    return null;
}

function cov_backend(array $cfg): string
{
    if ($cfg['imageBackend'] === 'imagick') return 'imagick';
    if ($cfg['imageBackend'] === 'gd') return 'gd';
    if (class_exists('Imagick')) return 'imagick';
    if (function_exists('imagecreatefromstring')) return 'gd';
    return 'none';
}

/** 指定形式で書き出せるか (webp が使えなければ jpeg に落とす) */
function cov_supported_format(string $fmt, array $cfg): string
{
    $backend = cov_backend($cfg);
    if ($backend === 'imagick') {
        $q = @Imagick::queryFormats(strtoupper($fmt === 'jpeg' ? 'JPEG' : $fmt));
        if (!empty($q)) return $fmt;
    } elseif ($backend === 'gd') {
        $fn = ['webp' => 'imagewebp', 'jpeg' => 'imagejpeg', 'png' => 'imagepng'][$fmt] ?? null;
        if ($fn !== null && function_exists($fn)) return $fmt;
    }
    return ($fmt === 'webp') ? cov_supported_format('jpeg', $cfg) : 'png';
}

function cov_format_ext(string $fmt): string
{
    return ['webp' => 'webp', 'jpeg' => 'jpg', 'png' => 'png'][$fmt] ?? $fmt;
}

/**
 * 最大解像度に収まるよう縮小しつつ指定形式にエンコードする。
 * 縮小が要らない場合も (passthrough でない限り) 形式を揃えるため再エンコードする。
 */
function cov_render(string $bytes, array $cfg, string $fmt, ?string &$err): ?array
{
    $err = null;
    $maxW = max(0, (int)$cfg['maxWidth']);
    $maxH = max(0, (int)$cfg['maxHeight']);
    $quality = max(1, min(100, (int)$cfg['quality']));
    $backend = cov_backend($cfg);

    if ($backend === 'imagick') {
        try {
            $im = new Imagick();
            $im->readImageBlob($bytes);
            if ($im->getNumberImages() > 1) {          // アニメーション GIF/WebP は 1 フレーム目
                $im->setFirstIterator();
                $first = $im->getImage();
                $im->clear();
                $im = $first;
            }
            $im->setImageOrientation(Imagick::ORIENTATION_TOPLEFT);
            $w = $im->getImageWidth(); $h = $im->getImageHeight();
            [$nw, $nh] = cov_fit($w, $h, $maxW, $maxH);
            if ($nw !== $w || $nh !== $h) $im->thumbnailImage($nw, $nh, true);
            if ($fmt === 'jpeg') {
                $bg = new Imagick();
                $bg->newImage($im->getImageWidth(), $im->getImageHeight(), 'white');
                $bg->compositeImage($im, Imagick::COMPOSITE_OVER, 0, 0);
                $im->clear();
                $im = $bg;
            }
            $im->setImageFormat($fmt);
            if ($fmt !== 'png') $im->setImageCompressionQuality($quality);
            $im->stripImage();
            $data = $im->getImageBlob();
            $ow = $im->getImageWidth(); $oh = $im->getImageHeight();
            $im->clear();
            return ['data' => $data, 'w' => $ow, 'h' => $oh];
        } catch (Throwable $e) {
            $err = 'imagick: ' . $e->getMessage();
            if ($cfg['imageBackend'] === 'imagick') return null;
            // auto なら GD で再挑戦する
        }
    }

    if (!function_exists('imagecreatefromstring')) {
        $err = $err ?? 'GD も Imagick も使えません';
        return null;
    }

    $src = @imagecreatefromstring($bytes);
    if ($src === false) { $err = ($err ? $err . ' / ' : '') . 'gd: デコードできません'; return null; }
    $w = imagesx($src); $h = imagesy($src);
    [$nw, $nh] = cov_fit($w, $h, $maxW, $maxH);

    $dst = imagecreatetruecolor($nw, $nh);
    if ($fmt === 'jpeg') {
        imagefill($dst, 0, 0, imagecolorallocate($dst, 255, 255, 255));
        imagealphablending($dst, true);
    } else {
        imagealphablending($dst, false);
        imagesavealpha($dst, true);
        imagefill($dst, 0, 0, imagecolorallocatealpha($dst, 0, 0, 0, 127));
    }
    imagecopyresampled($dst, $src, 0, 0, 0, 0, $nw, $nh, $w, $h);
    imagedestroy($src);
    if ($fmt !== 'jpeg') imagealphablending($dst, false);

    ob_start();
    if ($fmt === 'webp') imagewebp($dst, null, $quality);
    elseif ($fmt === 'jpeg') imagejpeg($dst, null, $quality);
    else imagepng($dst, null, 6);
    $data = ob_get_clean();
    imagedestroy($dst);

    if ($data === false || $data === '') { $err = 'gd: エンコードできません'; return null; }
    return ['data' => $data, 'w' => $nw, 'h' => $nh];
}

/** 最大幅・高さに収まる寸法 (拡大はしない) */
function cov_fit(int $w, int $h, int $maxW, int $maxH): array
{
    $scale = 1.0;
    if ($maxW > 0 && $w > $maxW) $scale = min($scale, $maxW / $w);
    if ($maxH > 0 && $h > $maxH) $scale = min($scale, $maxH / $h);
    if ($scale >= 1.0) return [$w, $h];
    return [max(1, (int)round($w * $scale)), max(1, (int)round($h * $scale))];
}

// ---------------------------------------------------------------- 設定 / 引数

function cov_usage(): void
{
    $t = <<<TXT
generate_coverimages.php — library.php 用の表紙画像を一括生成する

  php tools/generate_coverimages.php [オプション]

対象の指定
  --config=PATH     library.config.php のパス (既定: このスクリプトの1つ上の library.config.php)
  --root=PATH       設定の 'root' を上書きして別フォルダを処理する
  --path=REL        root 配下のサブフォルダだけを処理する
  --filter=STR      相対パスに STR を含むものだけ処理する (大小無視)
  --ext=LIST        対象拡張子を絞る (例: --ext=pdf,epub)
  --limit=N         最大 N 件だけ処理する

生成
  --format=FMT      出力形式 webp | jpeg | png            (既定: webp)
  --quality=N       webp / jpeg の品質 1-100              (既定: 82)
  --max-width=N     最大幅。超える画像だけ縮小。0 で無制限 (既定: 1200)
  --max-height=N    最大高さ                              (既定: 1600)
  --sort=MODE       書庫内のファイル名順 lexical | natural (既定: lexical)
  --epub-cover=SRC  EPUB の表紙 spine | metadata          (既定: spine)
  --dpi=N           PDF のレンダリング解像度               (既定: 150)
  --pdf-engine=E    auto | imagick | pdftoppm | mutool | magick | gs
  --backend=B       画像処理 auto | imagick | gd
  --passthrough     縮小不要なら再エンコードせず原本をそのまま置く

上書き / 日時
  --force           既存の表紙があっても作り直す
  --stale           表紙が元ファイルより古いときだけ作り直す
  --mtime           表紙の更新日時を抽出元ファイル (PDF/EPUB/書庫自体) に合わせる
  --no-mtime        設定で有効になっている --mtime を打ち消す

その他
  --dry-run         書き込まずに何をするか表示する
  --check           使えるバックエンド (Imagick/GD/PDF/7z 等) を表示して終了
  --console-encoding=ENC  コンソール出力の文字コード (Windows なら SJIS-win)
  -v, --verbose     詳細表示   -q, --quiet  最小表示   -h, --help  このヘルプ
TXT;
    cov_out($t);
}

/** 受け付けるオプション名 (タイプミスを黙って無視しないための一覧) */
const COVER_OPTIONS = [
    'help', 'verbose', 'quiet', 'config', 'root', 'path', 'filter', 'ext', 'limit',
    'format', 'quality', 'max-width', 'max-height', 'sort', 'epub-cover', 'dpi',
    'pdf-engine', 'backend', 'passthrough', 'force', 'stale', 'mtime', 'dry-run',
    'check', 'console-encoding',
];

/** --key=value / --flag / --no-flag / -v -q -h を解釈する */
function cov_parse_argv(array $argv): array
{
    $opts = [];
    for ($i = 1; $i < count($argv); $i++) {
        $a = $argv[$i];
        if ($a === '-h') { $opts['help'] = true; continue; }
        if ($a === '-v') { $opts['verbose'] = true; continue; }
        if ($a === '-q') { $opts['quiet'] = true; continue; }
        if (strncmp($a, '--', 2) !== 0) cov_die('不明な引数: ' . $a);
        $body = substr($a, 2);
        $eq = strpos($body, '=');
        if ($eq === false) {
            if (strncmp($body, 'no-', 3) === 0) $opts[substr($body, 3)] = false;
            else $opts[$body] = true;
        } else {
            $opts[substr($body, 0, $eq)] = substr($body, $eq + 1);
        }
    }
    foreach (array_keys($opts) as $k) {
        if (!in_array($k, COVER_OPTIONS, true)) cov_die('不明なオプション: --' . $k . ' (--help で一覧)');
    }
    return $opts;
}

function cov_bool($v, bool $default): bool
{
    if ($v === null) return $default;
    if (is_bool($v)) return $v;
    $s = strtolower(trim((string)$v));
    if ($s === '' || $s === '1' || $s === 'true' || $s === 'yes' || $s === 'on') return true;
    if ($s === '0' || $s === 'false' || $s === 'no' || $s === 'off') return false;
    return $default;
}

// ---------------------------------------------------------------- main

$opts = cov_parse_argv($argv);
if (!empty($opts['help'])) { cov_usage(); exit(0); }

$GLOBALS['cov_quiet']   = !empty($opts['quiet']);
$GLOBALS['cov_verbose'] = !empty($opts['verbose']);

// --- library.config.php を読む (無くても --root があれば動く) ---
$cfgFile = isset($opts['config']) && !is_bool($opts['config'])
    ? (string)$opts['config']
    : cov_norm_sep(dirname(__DIR__)) . '/library.config.php';
$user = [];
if (is_file($cfgFile)) {
    $user = require $cfgFile;
    if (!is_array($user)) cov_die($cfgFile . ' は設定の配列を return してください。');
} elseif (!isset($opts['root']) && empty($opts['check'])) {
    cov_die($cfgFile . " がありません。library.config.example.php をコピーするか --root=PATH を指定してください。");
}

$cfg = array_merge([
    'root'           => '',
    'exts'           => ['pdf', 'cbz', 'cbr', 'cb7', 'epub', 'zip', 'rar', '7z'],
    'coverSuffix'    => '.coverimage',
    'coverExts'      => ['webp', 'avif', 'png', 'jpg', 'jpeg', 'gif'],
    'maxDepth'       => 12,
    'followSymlinks' => false,
    'fsEncoding'     => '',
], is_array($user) ? $user : []);
$cfg = array_merge($cfg, COVER_DEFAULTS);
// library.config.php の 'coverTool' => [...] でツール側の既定を上書きできる
if (isset($user['coverTool']) && is_array($user['coverTool'])) {
    foreach ($user['coverTool'] as $k => $v) {
        if ($k === 'commands' && is_array($v)) $cfg['commands'] = array_merge($cfg['commands'], $v);
        else $cfg[$k] = $v;
    }
}

// --- コマンドラインで上書き ---
$map = [
    'format' => 'format', 'quality' => 'quality', 'max-width' => 'maxWidth', 'max-height' => 'maxHeight',
    'sort' => 'sort', 'epub-cover' => 'epubCover', 'dpi' => 'pdfDpi', 'pdf-engine' => 'pdfEngine',
    'backend' => 'imageBackend', 'console-encoding' => 'consoleEncoding',
];
foreach ($map as $opt => $key) {
    if (isset($opts[$opt]) && !is_bool($opts[$opt])) $cfg[$key] = $opts[$opt];
}
foreach (['quality', 'maxWidth', 'maxHeight', 'pdfDpi', 'maxCandidates'] as $k) $cfg[$k] = (int)$cfg[$k];
if (isset($opts['passthrough'])) $cfg['passthrough'] = cov_bool($opts['passthrough'], true);
if (isset($opts['mtime']))       $cfg['matchMtime']  = cov_bool($opts['mtime'], true);
$cfg['matchMtime']  = (bool)$cfg['matchMtime'];
$cfg['passthrough'] = (bool)$cfg['passthrough'];
if (isset($opts['root']) && !is_bool($opts['root'])) $cfg['root'] = (string)$opts['root'];

$cfg['exts']        = array_map('strtolower', (array)$cfg['exts']);
$cfg['coverExts']   = array_map('strtolower', (array)$cfg['coverExts']);
$cfg['coverSuffix'] = str_replace(['/', '\\', "\0"], '', (string)$cfg['coverSuffix']);
$cfg['sort']        = ($cfg['sort'] === 'natural') ? 'natural' : 'lexical';
$cfg['epubCover']   = ($cfg['epubCover'] === 'metadata') ? 'metadata' : 'spine';
$cfg['format']      = in_array($cfg['format'], ['webp', 'jpeg', 'jpg', 'png'], true)
    ? ($cfg['format'] === 'jpg' ? 'jpeg' : $cfg['format']) : 'webp';
$GLOBALS['cov_console_enc'] = (string)$cfg['consoleEncoding'];

if (isset($opts['ext']) && !is_bool($opts['ext'])) {
    $only = array_filter(array_map(function ($s) { return strtolower(trim($s, " \t.")); }, explode(',', (string)$opts['ext'])));
    $cfg['exts'] = array_values(array_intersect($cfg['exts'], $only));
    if (!$cfg['exts']) cov_die('--ext で指定した拡張子が設定の exts に含まれていません。');
}

$force   = !empty($opts['force']);
$stale   = !empty($opts['stale']);
$dryRun  = !empty($opts['dry-run']);
$limit   = (isset($opts['limit']) && !is_bool($opts['limit'])) ? max(0, (int)$opts['limit']) : 0;
$filter  = (isset($opts['filter']) && !is_bool($opts['filter'])) ? (string)$opts['filter'] : '';

// --- バックエンド確認 ---
$backend = cov_backend($cfg);
$outFmt  = ($backend === 'none') ? $cfg['format'] : cov_supported_format($cfg['format'], $cfg);
$outExt  = cov_format_ext($outFmt);

if (!empty($opts['check'])) {
    cov_out('PHP            : ' . PHP_VERSION . ' (' . PHP_OS_FAMILY . ')');
    cov_out('ZipArchive     : ' . (class_exists('ZipArchive') ? 'あり' : 'なし (cbz/zip/epub は 7z 頼み)'));
    cov_out('Imagick        : ' . (class_exists('Imagick') ? 'あり' : 'なし'));
    cov_out('GD             : ' . (function_exists('imagecreatefromstring') ? 'あり' : 'なし')
        . (function_exists('imagewebp') ? ' (webp 可)' : ' (webp 不可)'));
    cov_out('画像バックエンド: ' . $backend . ' / 出力: ' . $outFmt . ' (.' . $outExt . ')');
    foreach (['pdftoppm', 'mutool', 'magick', 'gs', '7z', 'unrar'] as $c) {
        $p = cov_which($c, $cfg);
        cov_out(str_pad($c, 15) . ': ' . ($p === null ? 'なし' : $p));
    }
    exit(0);
}

if ($backend === 'none') cov_die('Imagick も GD も使えません。php-gd を有効にしてください。');
if ($outFmt !== $cfg['format']) {
    cov_warn("注意: {$cfg['format']} で書き出せないため {$outFmt} を使います。");
}
if (!in_array($outExt, $cfg['coverExts'], true)) {
    cov_die("出力拡張子 .{$outExt} が coverExts に含まれていません。library.php が表紙として認識できないので、"
        . "--format を変えるか library.config.php の 'coverExts' に追加してください。");
}

// --- root の解決 ---
if ($cfg['root'] === '') cov_die("'root' が設定されていません (library.config.php または --root=PATH)。");
$rootReal = realpath((string)$cfg['root']);
if ($rootReal === false || !is_dir($rootReal)) cov_die('root フォルダが見つかりません: ' . $cfg['root']);
$rootReal = cov_norm_sep($rootReal);

$scanRoot = $rootReal;
if (isset($opts['path']) && !is_bool($opts['path'])) {
    $sub = realpath($rootReal . '/' . trim(str_replace('\\', '/', (string)$opts['path']), '/'));
    if ($sub === false || !is_dir($sub)) cov_die('--path のフォルダが見つかりません: ' . $opts['path']);
    $sub = cov_norm_sep($sub);
    if ($sub !== $rootReal && strncmp($sub, $rootReal . '/', strlen($rootReal) + 1) !== 0) {
        cov_die('--path は root の外を指しています。');
    }
    $scanRoot = $sub;
}

// ---------------------------------------------------------------- 走査

/** root 以下を再帰的に走査して対象ファイルの実パスを集める (library.php の lib_walk と同じ規則) */
function cov_walk(string $dir, array $cfg, array &$out, array &$seen, int $depth): void
{
    if ($depth > (int)$cfg['maxDepth']) return;
    $names = @scandir($dir);
    if ($names === false) { cov_warn('読み取れないフォルダ: ' . cov_to_utf8($dir, $cfg['fsEncoding'])); return; }
    sort($names, SORT_STRING);

    foreach ($names as $name) {
        if ($name === '.' || $name === '..' || $name[0] === '.') continue;
        $path = $dir . '/' . $name;
        if (!$cfg['followSymlinks'] && is_link($path)) continue;
        if (is_dir($path)) {
            $real = realpath($path);
            if ($real === false) continue;
            $real = cov_norm_sep($real);
            if (isset($seen[$real])) continue;                // symlink ループ対策
            $seen[$real] = true;
            cov_walk($path, $cfg, $out, $seen, $depth + 1);
        } elseif (is_file($path)) {
            if (in_array(cov_ext($name), $cfg['exts'], true)) $out[] = $path;
        }
    }
}

$files = [];
$seenDirs = [$scanRoot => true];
cov_walk($scanRoot, $cfg, $files, $seenDirs, 0);

cov_info('root      : ' . cov_to_utf8($rootReal, $cfg['fsEncoding']));
cov_info('対象       : ' . count($files) . ' 件 (' . implode(', ', $cfg['exts']) . ')');
cov_info('出力       : ' . $cfg['coverSuffix'] . '.' . $outExt . ' / ' . $outFmt
    . ' q' . $cfg['quality'] . ' / 最大 ' . ($cfg['maxWidth'] ?: '∞') . 'x' . ($cfg['maxHeight'] ?: '∞')
    . ' / sort=' . $cfg['sort'] . ' / epub=' . $cfg['epubCover']
    . ($cfg['matchMtime'] ? ' / mtime同期' : '') . ($dryRun ? ' / DRY-RUN' : ''));
cov_info('');

// ---------------------------------------------------------------- 1 ファイル分の処理

/**
 * 表紙にする画像バイト列を選ぶ。
 * 戻り値 ['bytes'=>..., 'ext'=>元の拡張子 or null, 'src'=>説明用の出所]
 */
function cov_extract(string $full, string $ext, array $cfg, ?string &$err): ?array
{
    $err = null;

    if ($ext === 'pdf') {
        $bytes = cov_pdf_first_page($full, $cfg, $err);
        if ($bytes === null) return null;
        return ['bytes' => $bytes, 'ext' => null, 'src' => 'p.1'];
    }

    $ar = cov_archive_open($full, $ext, $cfg);
    if ($ar === null) {
        $need = in_array($ext, ['cbr', 'rar'], true) ? '7z / unrar' : ($ext === 'cb7' || $ext === '7z' ? '7z' : 'ZipArchive');
        $err = '書庫を開けません (' . $need . ' が必要かもしれません)';
        return null;
    }

    try {
        $imageNames = array_values(array_filter($ar['names'], 'cov_is_page_image'));
        if (!$imageNames) { $err = '画像ファイルが見つかりません'; return null; }

        $limit = max(1, (int)$cfg['maxCandidates']);
        $candidates = [];
        $srcLabel = 'ファイル名順(' . $cfg['sort'] . ')';

        if ($ext === 'epub') {
            $note = null;
            $candidates = cov_epub_candidates($ar, $imageNames, $cfg, $limit, $note);
            if ($candidates) {
                $srcLabel = 'EPUB ' . ($cfg['epubCover'] === 'metadata' ? 'metadata/' : '') . '読み順';
                if ($note !== null) $srcLabel .= ' (' . $note . ')';
            } elseif ($note !== null) {
                cov_verbose('    EPUB 構造解析: ' . $note . ' → ファイル名順にフォールバック');
            }
        }

        // 構造解析で取れなかった / EPUB 以外はファイル名順
        $sorted = cov_sort_names($imageNames, $cfg['sort']);
        foreach ($sorted as $n) {
            if (count($candidates) >= $limit * 2) break;
            if (!in_array($n, $candidates, true)) $candidates[] = $n;
        }

        // 先頭から順に読んで、画像として認識できた最初の 1 枚を採用する
        $tried = 0;
        foreach ($candidates as $n) {
            if ($tried >= $limit) break;
            $tried++;
            $bytes = cov_archive_read($ar, $n);
            if ($bytes === null || $bytes === '') continue;
            if (cov_probe($bytes) === null) {
                cov_verbose('    デコードできないので次の候補へ: ' . $n);
                continue;
            }
            return ['bytes' => $bytes, 'ext' => cov_ext($n), 'src' => $srcLabel . ' ' . $n];
        }
        $err = '画像を読み出せません (' . $tried . ' 候補を試行)';
        return null;
    } finally {
        cov_archive_close($ar);
    }
}

$stats = ['made' => 0, 'skip' => 0, 'fail' => 0];
$index = 0;
$started = microtime(true);

foreach ($files as $full) {
    $rel = ltrim(substr(cov_norm_sep($full), strlen($rootReal)), '/');
    $relDisp = cov_to_utf8($rel, $cfg['fsEncoding']);
    if ($filter !== '' && stripos($relDisp, $filter) === false && stripos($rel, $filter) === false) continue;
    if ($limit > 0 && $stats['made'] >= $limit) break;

    $index++;
    $dir  = dirname($full);
    $name = basename($full);
    $ext  = cov_ext($name);
    $srcMtime = @filemtime($full);
    if ($srcMtime === false) $srcMtime = 0;

    // --- 既存の表紙 (どの拡張子でも) を探す ---
    $existing = null;
    foreach ($cfg['coverExts'] as $ce) {
        $cand = $dir . '/' . $name . $cfg['coverSuffix'] . '.' . $ce;
        if (is_file($cand)) { $existing = $cand; break; }
    }
    if ($existing !== null && !$force) {
        $needs = $stale && $srcMtime > 0 && (@filemtime($existing) ?: 0) < $srcMtime;
        if (!$needs) {
            // mtime 同期だけは既存ファイルにも当てておく (--mtime を後から付けたケース)
            if ($cfg['matchMtime'] && $srcMtime > 0 && (@filemtime($existing) ?: 0) !== $srcMtime && !$dryRun) {
                @touch($existing, $srcMtime);
                cov_verbose('  [mtime] ' . $relDisp);
            }
            $stats['skip']++;
            cov_verbose('  [skip] ' . $relDisp . ' (' . basename(cov_to_utf8($existing, $cfg['fsEncoding'])) . ')');
            continue;
        }
    }

    $err = null;
    $picked = cov_extract($full, $ext, $cfg, $err);
    if ($picked === null) {
        $stats['fail']++;
        cov_warn('  [NG]   ' . $relDisp . ' — ' . ($err ?? '不明なエラー'));
        continue;
    }

    // --- 縮小 / エンコード ---
    $writeExt = $outExt;
    $data = null; $dim = '';
    $probe = cov_probe($picked['bytes']);
    $needResize = ($probe !== null)
        && (($cfg['maxWidth'] > 0 && $probe['w'] > $cfg['maxWidth']) || ($cfg['maxHeight'] > 0 && $probe['h'] > $cfg['maxHeight']));

    // passthrough: 縮小不要 & 元が書庫内の画像 & その拡張子が coverExts にある → 再エンコードしない
    $canPass = $cfg['passthrough'] && !$needResize && $picked['ext'] !== null
        && in_array($picked['ext'], $cfg['coverExts'], true);
    if ($canPass) {
        $data = $picked['bytes'];
        $writeExt = $picked['ext'];
        $dim = $probe ? ($probe['w'] . 'x' . $probe['h']) : '?';
    } else {
        $renderErr = null;
        $r = cov_render($picked['bytes'], $cfg, $outFmt, $renderErr);
        if ($r === null) {
            // デコードできない形式 (jxl/heic 等) でも、そのまま置ける形式なら原本を使う
            if ($picked['ext'] !== null && in_array($picked['ext'], $cfg['coverExts'], true) && !$needResize) {
                $data = $picked['bytes'];
                $writeExt = $picked['ext'];
                $dim = $probe ? ($probe['w'] . 'x' . $probe['h']) : '?';
                cov_verbose('    再エンコード不可 (' . $renderErr . ') → 原本をそのまま使用');
            } else {
                $stats['fail']++;
                cov_warn('  [NG]   ' . $relDisp . ' — ' . ($renderErr ?? '画像を変換できません'));
                continue;
            }
        } else {
            $data = $r['data'];
            $dim = $r['w'] . 'x' . $r['h'];
        }
    }

    $coverName = $name . $cfg['coverSuffix'] . '.' . $writeExt;
    $coverPath = $dir . '/' . $coverName;
    $tag = ($existing !== null) ? '再生成' : '生成';

    if ($dryRun) {
        $stats['made']++;
        cov_info(sprintf('  [dry]  %s -> %s (%s, %s) %s', $relDisp, $coverName, $dim, cov_human_size(strlen($data)), $picked['src']));
        continue;
    }

    // 同じフォルダに一時ファイルを作ってから rename (途中で落ちても半端な表紙を残さない)。
    // '.' 始まりなので library.php の走査にも引っかからない。
    $tmp = $dir . '/.covertmp_' . getmypid() . '_' . $index;
    if (@file_put_contents($tmp, $data) === false) {
        $stats['fail']++;
        cov_warn('  [NG]   ' . $relDisp . ' — 書き込めません: ' . cov_to_utf8($dir, $cfg['fsEncoding']));
        @unlink($tmp);
        continue;
    }
    if (!@rename($tmp, $coverPath)) {
        // Windows では上書き rename が失敗することがあるので、消してからもう一度
        @unlink($coverPath);
        if (!@rename($tmp, $coverPath)) {
            $stats['fail']++;
            cov_warn('  [NG]   ' . $relDisp . ' — 表紙を配置できません');
            @unlink($tmp);
            continue;
        }
    }

    // 別拡張子の古い表紙が残っていると library.php が coverExts の優先順で拾ってしまうので消す
    foreach ($cfg['coverExts'] as $ce) {
        if ($ce === $writeExt) continue;
        $old = $dir . '/' . $name . $cfg['coverSuffix'] . '.' . $ce;
        if (is_file($old)) { @unlink($old); cov_verbose('    古い表紙を削除: ' . $ce); }
    }

    // 表紙の更新日時を「抽出元ファイル自体」に合わせる (書庫内画像の日時ではない点に注意)
    if ($cfg['matchMtime'] && $srcMtime > 0) @touch($coverPath, $srcMtime);

    $stats['made']++;
    cov_info(sprintf('  [%s] %s -> %s (%s, %s)', $tag, $relDisp, $coverName, $dim, cov_human_size(strlen($data))));
    cov_verbose('    出所: ' . $picked['src']);
}

$elapsed = microtime(true) - $started;
cov_info('');
cov_info(sprintf('完了: 生成 %d / スキップ %d / 失敗 %d  (%.1fs)%s',
    $stats['made'], $stats['skip'], $stats['fail'], $elapsed, $dryRun ? ' ※DRY-RUN' : ''));
exit($stats['fail'] > 0 ? 2 : 0);
