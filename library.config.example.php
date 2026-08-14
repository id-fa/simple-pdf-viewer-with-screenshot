<?php
/**
 * library.php の設定サンプル。
 * このファイルを library.config.php にコピーして 'root' を書き換えてください。
 * (library.config.php は .gitignore 済み)
 */

return [
    // 公開したいフォルダの絶対パス。
    // DOCUMENT_ROOT の **外** に置くのを推奨。外に置けば直リンク用の URL が存在しないので、
    // このアプリ (library.php) 経由以外でファイルを取得する手段が無くなる。
    // 例 (Linux):   '/srv/library'
    // 例 (Windows): 'D:/Books'
    'root' => '/srv/library',

    // 一覧のルート表示名。空なら root のフォルダ名を使う。
    'label' => '',

    // Basic 認証。null なら認証なし (LAN 内限定で使う場合はこのままでよい)。
    // サーバー設定 (.htaccess 等) は不要で、認証の影響範囲が library.php に閉じるため、
    // Service Worker のプリキャッシュを巻き込んでオフライン動作を壊す事故が起きない。
    // インターネットに公開するなら必ず設定し、かつ HTTPS で運用すること
    // (Basic 認証は資格情報を base64 で毎リクエスト送るだけなので、平文 HTTP では丸見え)。
    //
    //   'auth' => ['user' => 'yourname', 'pass' => 'yourpassword', 'realm' => 'Library'],
    //
    // 'pass' には password_hash() の出力も書ける ('$2y$...' / '$argon2...' で始まる文字列は
    // ハッシュとみなして password_verify で照合する)。ハッシュの作り方:
    //   php -r "echo password_hash('yourpassword', PASSWORD_DEFAULT), PHP_EOL;"
    'auth' => null,

    // 配信を許可する拡張子。ここに無いものは一覧にも出ないし個別取得もできない。
    // pdf-viewer.html から使う場合もサーバー側は共通でよい (クライアント側が .pdf に絞る)。
    'exts' => ['pdf', 'cbz', 'cbr', 'cb7', 'epub', 'zip', 'rar', '7z'],

    // 表紙画像 (サイドカー) の命名規則。
    // 「元のファイル名 + coverSuffix + 画像拡張子」で置いておくと、一覧のマウスオーバー /
    // ロングタップでプレビューでき、サムネイル表示にも使われる。自動生成はしない。
    //
    //   Manga/vol01.cbz              ← 本
    //   Manga/vol01.cbz.coverimage.webp  ← その表紙
    //
    // 元のファイル名を丸ごと残すので vol01.cbz と vol01.pdf が同居しても衝突しない。
    // coverExts は先に見つかったものを使う (優先順)。
    'coverSuffix' => '.coverimage',
    'coverExts'   => ['webp', 'avif', 'png', 'jpg', 'jpeg', 'gif'],

    // 一覧の最大件数。超えると打ち切って warnings で通知する。
    'maxEntries' => 5000,

    // 再帰の最大深さ。
    'maxDepth' => 12,

    // シンボリックリンクを辿るか。true にしても realpath 検証で root 外へは出られないが、
    // 既定は false (リンク先の実体が root 内にある場合のみ true を検討)。
    'followSymlinks' => false,

    // ファイルシステムのファイル名エンコーディング。**基本は '' のまま**。
    // Linux / macOS / NAS は UTF-8。Windows 版 PHP も 7.1 以降は default_charset=UTF-8 なら
    // scandir が UTF-8 を返す (PHP 8.4 / Windows で実測) ので '' で正しく動く。
    // 'SJIS-win' を指定するのは「一覧のファイル名が化ける」もしくは tree の warnings に
    // 「UTF-8 として解釈できないファイル名を N 件除外しました」が出たときだけ。
    // (念のため library.php 側で「既に UTF-8 なら変換しない」ようにしてあるので、
    //  誤って指定しても化けないが、必要が無いなら空のままが確実)
    'fsEncoding' => '',
];
