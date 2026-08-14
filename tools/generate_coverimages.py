#!/usr/bin/env python3
"""generate_coverimages.py — library.php 用の表紙画像 (サイドカー) を一括生成する CLI ツール。

tools/generate_coverimages.php の Python 版。**Windows では PHP に Imagick / Ghostscript を
入れるのが面倒**なのに対し、Python は pip だけで PDF レンダリングまで揃うので、こちらの方が
導入が楽なことが多い (Linux ではどちらでもよい)。

PHP 版との違い:
  - **library.config.php は読まない。** 設定は --root などのコマンドラインオプション、
    または --config=cover.json (JSON) で渡す
  - PDF は PyMuPDF / pypdfium2 があれば **外部コマンド無しで**レンダリングできる
  - ファイル名は Python が Unicode で扱うので fsEncoding 相当の設定は不要

表紙の決め方 (comic-viewer.html / PHP 版と同じ):
  PDF   … 1 ページ目をレンダリング
  EPUB  … 構造解析 (container.xml → OPF → spine → XHTML の <img> / <svg><image> /
          背景 url()) で求めた **読み順の 1 枚目**。spine で取れなければ manifest の
          image/* 記述順 → ファイル名順
  書庫  … ファイル名順 (既定 lexical / --sort=natural) の 1 ファイル目

命名規則も library.php と同じ:
    Manga/vol01.cbz                    ← 本
    Manga/vol01.cbz.coverimage.webp    ← その表紙

使い方:
    python tools/generate_coverimages.py --root=D:/Books --check
    python tools/generate_coverimages.py --root=D:/Books --dry-run -v
    python tools/generate_coverimages.py --root=D:/Books --mtime
    python tools/generate_coverimages.py --help

必要なもの:
    pip install pillow            # 必須 (画像のデコード・縮小・書き出し)
    pip install pypdfium2         # PDF (推奨。BSD/Apache 系ライセンス)
    pip install pymupdf           # PDF (別実装。AGPL なので用途に注意)
    pip install py7zr rarfile     # 7z / RAR (無ければ外部の 7z / unrar を使う)
    # CBZ / ZIP / EPUB は標準ライブラリだけで読める
"""

from __future__ import annotations

import argparse
import glob as globmod
import io
import json
import os
import re
import shutil
import subprocess
import sys
import time
from html.parser import HTMLParser
from typing import Dict, List, NoReturn, Optional, Sequence, Tuple
from urllib.parse import unquote
import xml.etree.ElementTree as ET
import zipfile

try:                                   # Pillow は必須。無いときは --check だけ動かせるようにする
    from PIL import Image, ImageOps, features as pil_features
except Exception:                      # pragma: no cover
    Image = ImageOps = pil_features = None   # type: ignore[assignment]

# ---------------------------------------------------------------- 既定値

DEFAULTS = {
    # 対象にする拡張子 (library.config.php の 'exts' と同じ意味)
    "exts": ["pdf", "cbz", "cbr", "cb7", "epub", "zip", "rar", "7z"],
    # 表紙サイドカーの命名規則 (library.php と一致させること)
    "coverSuffix": ".coverimage",
    "coverExts": ["webp", "avif", "png", "jpg", "jpeg", "gif"],
    # 出力
    "format": "webp",          # webp | jpeg | png
    "quality": 82,
    "maxWidth": 1200,          # これを超える画像だけ縮小する (0 で無制限)
    "maxHeight": 1600,
    # 選び方
    "sort": "lexical",         # lexical | natural
    "epubCover": "spine",      # spine | metadata
    "maxCandidates": 5,
    # 動作
    "matchMtime": False,
    "passthrough": False,
    "maxDepth": 12,
    "followSymlinks": False,
    # PDF
    "pdfDpi": 150,
    "pdfEngine": "auto",       # auto | pymupdf | pypdfium2 | pdftoppm | mutool | magick | gs
    # 外部コマンド (PATH に無ければ絶対パスを書く)
    "commands": {"pdftoppm": "pdftoppm", "mutool": "mutool", "magick": "magick",
                 "gs": "gs", "7z": "7z", "unrar": "unrar"},
}

# comic-viewer.html の IMAGE_EXTS と同じ
IMAGE_RE = re.compile(r"\.(jpe?g|png|webp|gif|bmp|avif|jxl|tiff?|heic|heif)$", re.I)

ZIP_EXTS = {"zip", "cbz", "epub"}
RAR_EXTS = {"rar", "cbr"}
SEVEN_EXTS = {"7z", "cb7"}

XLINK_NS = "http://www.w3.org/1999/xlink"

QUIET = False
VERBOSE = False


# ---------------------------------------------------------------- 出力

def setup_console(enc: str = "") -> None:
    """Windows の cp932 コンソールで日本語のファイル名を出しても落ちないようにする"""
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding=enc or stream.encoding, errors="replace")  # type: ignore[union-attr]
        except Exception:
            pass


def info(msg: str) -> None:
    if not QUIET:
        print(msg, flush=True)


def verbose(msg: str) -> None:
    if VERBOSE and not QUIET:
        print(msg, flush=True)


def warn(msg: str) -> None:
    print(msg, file=sys.stderr, flush=True)


def die(msg: str) -> NoReturn:
    warn("エラー / Error: " + msg)
    sys.exit(1)


def human_size(n: int) -> str:
    if n >= 1048576:
        return "%.1fMB" % (n / 1048576)
    if n >= 1024:
        return "%.0fKB" % (n / 1024)
    return "%dB" % n


def clip(s: str, limit: int = 200) -> str:
    s = re.sub(r"\s+", " ", s).strip()
    return s if len(s) <= limit else s[:limit] + "…"


# ---------------------------------------------------------------- パス / 並び順

def ext_of(name: str) -> str:
    _, dot, e = name.rpartition(".")
    return e.lower() if dot else ""


def sanitize_path(name: str) -> str:
    """'..' / '.' / 先頭スラッシュを落とす (comic-viewer.html の sanitizePath と同じ)"""
    parts = [seg for seg in name.replace("\\", "/").split("/") if seg not in ("", ".", "..")]
    return "/".join(parts)


def norm_key(p: str) -> str:
    """書庫エントリ名 ⇄ OPF/XHTML の href を突き合わせるための正規化キー"""
    try:
        p = unquote(p)
    except Exception:
        pass
    return sanitize_path(p).lower()


def dir_of(path: str) -> str:
    i = path.rfind("/")
    return "" if i < 0 else path[:i]


def resolve_href(base_dir: str, href: str) -> str:
    """href を基準ディレクトリから解決して正規化キーにする (fragment 除去 + ../ 解決)"""
    h = re.sub(r"[#?].*$", "", (href or "").replace("\\", "/"), flags=re.S)
    try:
        h = unquote(h)
    except Exception:
        pass
    if not h:
        return ""
    parts: List[str] = [] if (h.startswith("/") or not base_dir) else base_dir.split("/")
    for seg in h.split("/"):
        if seg in ("", "."):
            continue
        if seg == "..":
            if parts:
                parts.pop()
        else:
            parts.append(seg)
    return "/".join(parts).lower()


_NAT_RE = re.compile(r"(\d+)|(\D+)")


def natural_key(name: str) -> List[Tuple[int, object]]:
    """comic-viewer.html の naturalCompare 相当のソートキー"""
    out: List[Tuple[int, object]] = []
    for m in _NAT_RE.finditer(name):
        tok = m.group(0)
        out.append((0, int(tok)) if tok.isdigit() else (1, tok))
    return out


def is_page_image(name: str) -> bool:
    """macOS のリソースフォーク等、ページ画像ではないものを落とす"""
    if not IMAGE_RE.search(name):
        return False
    if "__macosx/" in name.lower():
        return False
    base = name.rsplit("/", 1)[-1]
    return not base.startswith("._") and not base.startswith(".")


# ---------------------------------------------------------------- 外部コマンド

_WHICH_CACHE: Dict[str, Optional[str]] = {}

# Windows のインストーラは PATH を通さないことが多いので定番の場所も見る
_WIN_HINTS = {
    "7z": ["7-Zip/7z.exe"],
    "unrar": ["WinRAR/UnRAR.exe", "UnRAR/UnRAR.exe"],
    "pdftoppm": ["poppler/bin/pdftoppm.exe", "poppler*/Library/bin/pdftoppm.exe", "poppler*/bin/pdftoppm.exe"],
    "mutool": ["mupdf*/mutool.exe"],
    "magick": ["ImageMagick*/magick.exe"],
    "gs": ["gs/*/bin/gswin64c.exe", "gs/*/bin/gswin32c.exe"],
}


def which(key: str, cfg: dict) -> Optional[str]:
    """コマンドの実体を探す (**存在確認のために起動はしない**)。結果はキャッシュする。

    PHP 版では `magick -v` のような試し起動をしていたが、引数を認識しないコマンドが
    対話モードに入って固まった。ここでは PATH を引くだけにしてある。
    """
    if key in _WHICH_CACHE:
        return _WHICH_CACHE[key]

    name = (cfg.get("commands") or {}).get(key, key)
    found = shutil.which(name)
    if found is None and os.path.isfile(name):
        found = name
    if found is None and os.name == "nt":
        for base in ("C:/Program Files", "C:/Program Files (x86)"):
            for rel in _WIN_HINTS.get(key, []):
                pat = base + "/" + rel
                hits = sorted(globmod.glob(pat)) if "*" in pat else ([pat] if os.path.isfile(pat) else [])
                if hits:
                    found = hits[0].replace("\\", "/")
                    break
            if found:
                break
    _WHICH_CACHE[key] = found
    return found


def run(cmd: Sequence[str], timeout: int = 300) -> Tuple[int, bytes, str]:
    """外部コマンドを実行する。stdout はバイナリのまま返す"""
    try:
        p = subprocess.run(list(cmd), stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                           stderr=subprocess.PIPE, timeout=timeout)
    except FileNotFoundError:
        return -1, b"", "コマンドが見つかりません"
    except subprocess.TimeoutExpired:
        return -1, b"", "タイムアウト (%ds)" % timeout
    except OSError as e:
        return -1, b"", str(e)
    return p.returncode, p.stdout, p.stderr.decode("utf-8", "replace")


# ---------------------------------------------------------------- 書庫アクセス
# 拡張子が偽装されていても (中身が rar の .cbz 等) 開けるよう、複数のバックエンドを順に試す。

class Archive:
    """entries: [(エントリ名, 並べ替え用のバイト列)]"""

    kind = "?"

    def __init__(self, path: str):
        self.path = path
        self.entries: List[Tuple[str, bytes]] = []

    @property
    def names(self) -> List[str]:
        return [n for n, _ in self.entries]

    def read(self, name: str) -> Optional[bytes]:
        raise NotImplementedError

    def close(self) -> None:
        pass


class ZipArc(Archive):
    kind = "zip"

    def __init__(self, path: str):
        super().__init__(path)
        self.zf = zipfile.ZipFile(path)
        for zi in self.zf.infolist():
            if zi.is_dir():
                continue
            # UTF-8 フラグが無い ZIP は cp437 でデコードされているので、生バイトに戻して
            # 並べ替えのキーにする (PHP 版のバイト列ソートと同じ順序にするため)
            if zi.flag_bits & 0x800:
                raw = zi.filename.encode("utf-8", "replace")
            else:
                raw = zi.filename.encode("cp437", "replace")
            self.entries.append((zi.filename, raw))

    def read(self, name: str) -> Optional[bytes]:
        try:
            return self.zf.read(name)
        except Exception:
            return None

    def close(self) -> None:
        try:
            self.zf.close()
        except Exception:
            pass


class Py7zrArc(Archive):
    kind = "py7zr"

    def __init__(self, path: str):
        super().__init__(path)
        import py7zr  # 遅延 import (未インストールなら呼び出し側が次のバックエンドへ)

        self.sz = py7zr.SevenZipFile(path)
        for fi in self.sz.list():
            if fi.is_directory:
                continue
            n = fi.filename.replace("\\", "/")
            self.entries.append((n, n.encode("utf-8", "replace")))

    def read(self, name: str) -> Optional[bytes]:
        import tempfile

        try:
            self.sz.reset()  # 同じアーカイブから複数回読むのに必要
            legacy_read = getattr(self.sz, "read", None)
            if callable(legacy_read):
                # py7zr 0.x: メモリに取れる
                got = legacy_read([name])
                if got:
                    return next(iter(got.values())).read()
                return None
            # py7zr 1.x は read() が廃止され extract() だけになったので一時フォルダ経由で読む
            with tempfile.TemporaryDirectory(prefix="cov7z_") as td:
                self.sz.extract(path=td, targets=[name])
                p = os.path.join(td, *sanitize_path(name).split("/"))
                if not os.path.isfile(p):
                    hits = sorted(globmod.glob(os.path.join(td, "**", "*"), recursive=True))
                    hits = [h for h in hits if os.path.isfile(h)]
                    if not hits:
                        return None
                    p = hits[0]
                with open(p, "rb") as fh:
                    return fh.read()
        except Exception:
            return None

    def close(self) -> None:
        try:
            self.sz.close()
        except Exception:
            pass


class RarfileArc(Archive):
    kind = "rarfile"

    def __init__(self, path: str):
        super().__init__(path)
        import rarfile  # 遅延 import

        self.rf = rarfile.RarFile(path)
        for ri in self.rf.infolist():
            if ri.is_dir():
                continue
            n = ri.filename.replace("\\", "/")
            self.entries.append((n, n.encode("utf-8", "replace")))

    def read(self, name: str) -> Optional[bytes]:
        try:
            return self.rf.read(name)
        except Exception:
            return None

    def close(self) -> None:
        try:
            self.rf.close()
        except Exception:
            pass


class CmdArc(Archive):
    """外部の 7z / unrar を使う。py7zr / rarfile が無い環境向け"""

    def __init__(self, path: str, tool: str, binpath: str):
        super().__init__(path)
        self.kind = tool
        self.bin = binpath
        if tool == "7z":
            _, out, err = run([binpath, "l", "-ba", "-slt", "-sccUTF-8", "-p", "--", path])
            names = parse_7z_list(out.decode("utf-8", "replace"))
        else:
            _, out, err = run([binpath, "lb", "-p-", "--", path])
            names = [ln.replace("\\", "/").strip()
                     for ln in out.decode("utf-8", "replace").splitlines()
                     if ln.strip() and not ln.strip().endswith("/")]
        if not names:
            raise OSError(err or "一覧を取得できません")
        self.entries = [(n, n.encode("utf-8", "replace")) for n in names]

    def read(self, name: str) -> Optional[bytes]:
        if self.kind == "7z":
            _, out, _ = run([self.bin, "x", "-so", "-y", "-p", "--", self.path, name])
        else:
            _, out, _ = run([self.bin, "p", "-inul", "-y", "-p-", "--", self.path, name])
        return out or None


def parse_7z_list(out: str) -> List[str]:
    """`7z l -ba -slt` の出力から実ファイルのパスだけ取り出す"""
    names: List[str] = []
    cur: Optional[str] = None
    is_dir = has_attr = False
    for line in out.splitlines() + [""]:
        if not line.strip():
            if cur is not None and not is_dir and has_attr:
                names.append(cur)
            cur, is_dir, has_attr = None, False, False
            continue
        key, sep, val = line.partition(" = ")
        if not sep:
            continue
        if key == "Path":
            cur = val.replace("\\", "/")
        elif key == "Attributes":
            has_attr = True
            if "D" in val:
                is_dir = True
        elif key == "Folder":
            has_attr = True
            if val == "+":
                is_dir = True
    return names


def open_archive(path: str, ext: str, cfg: dict) -> Optional[Archive]:
    if ext in ZIP_EXTS:
        order = ["zip", "py7zr", "cmd7z", "rarfile", "cmdunrar"]
    elif ext in RAR_EXTS:
        order = ["rarfile", "cmd7z", "cmdunrar", "zip"]
    else:
        order = ["py7zr", "cmd7z", "zip", "rarfile", "cmdunrar"]

    for kind in order:
        try:
            if kind == "zip":
                arc: Archive = ZipArc(path)
            elif kind == "py7zr":
                arc = Py7zrArc(path)
            elif kind == "rarfile":
                arc = RarfileArc(path)
            elif kind == "cmd7z":
                b = which("7z", cfg)
                if not b:
                    continue
                arc = CmdArc(path, "7z", b)
            else:
                b = which("unrar", cfg)
                if not b:
                    continue
                arc = CmdArc(path, "unrar", b)
        except Exception:
            continue
        if arc.entries:
            return arc
        arc.close()
    return None


# ---------------------------------------------------------------- EPUB 構造解析
# comic-viewer.html の analyzeEpub() の移植。ページ順の組み立てまではせず、
# 「読み順の先頭画像」を数枚だけ拾ったら打ち切る (表紙が欲しいだけなので全 spine は読まない)。

def local_name(tag: str) -> str:
    return tag.rpartition("}")[2].lower()


def parse_xml(data: bytes) -> Optional[ET.Element]:
    try:
        return ET.fromstring(data)
    except Exception:
        return None


def xml_find_all(root: ET.Element, name: str) -> List[ET.Element]:
    return [el for el in root.iter() if local_name(el.tag) == name]


def xml_attr(el: ET.Element, name: str) -> str:
    """名前空間の有無に関わらず属性を引く (xlink:href / href を同じに扱う)"""
    for k, v in el.attrib.items():
        if local_name(k) == name:
            return v
    return ""


class _ImgCollector(HTMLParser):
    """XML として壊れている XHTML 用のフォールバック。img / image / style を文書順に拾う"""

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.hits: List[Tuple[str, Dict[str, str]]] = []

    def handle_starttag(self, tag: str, attrs) -> None:
        d = {local_name(k): (v or "") for k, v in attrs}
        self.hits.append((tag.rpartition(":")[2].lower(), d))

    handle_startendtag = handle_starttag  # type: ignore[assignment]


def doc_elements(data: bytes) -> List[Tuple[str, Dict[str, str]]]:
    """(タグ名, 属性) を文書順に返す。XML で読めなければ HTML パーサで拾い直す"""
    root = parse_xml(data)
    if root is not None:
        out = []
        for el in root.iter():
            out.append((local_name(el.tag), {local_name(k): v for k, v in el.attrib.items()}))
        return out
    p = _ImgCollector()
    try:
        p.feed(data.decode("utf-8", "replace"))
    except Exception:
        pass
    return p.hits


def extract_doc_images(data: bytes, doc_path: str) -> List[str]:
    """XHTML / SVG 内の画像参照を文書順に返す (epubExtractDocImages 相当)"""
    base = dir_of(doc_path)
    out: List[str] = []

    def push(h: str) -> None:
        if not h or re.match(r"^(data:|https?:|blob:|#)", h, re.I):
            return
        p = resolve_href(base, h)
        if p:
            out.append(p)

    for tag, attrs in doc_elements(data):
        if tag in ("img", "image"):
            href = attrs.get("src") or attrs.get("href") or ""
            if href:
                push(href)
                continue
        # 固定レイアウト EPUB は背景画像で組まれていることがある
        style = attrs.get("style") or ""
        if "url(" in style:
            m = re.search(r"url\(\s*['\"]?([^'\")]+)", style, re.I)
            if m:
                push(m.group(1))
    return out


def epub_candidates(arc: Archive, image_names: List[str], cfg: dict,
                    limit: int) -> Tuple[List[str], Optional[str]]:
    """EPUB の読み順から表紙候補 (書庫内の実エントリ名) を最大 limit 件返す"""
    by_key = {norm_key(n): n for n in arc.names}
    img_keys = {norm_key(n): n for n in image_names}

    def read_key(key: str) -> Optional[bytes]:
        real = by_key.get(key)
        return arc.read(real) if real else None

    # --- OPF を特定 ---
    opf_path = ""
    container = read_key("meta-inf/container.xml")
    if container:
        root = parse_xml(container)
        if root is not None:
            for rf in xml_find_all(root, "rootfile"):
                fp = xml_attr(rf, "full-path")
                if fp:
                    opf_path = resolve_href("", fp)
                    break
    if opf_path not in by_key:
        opf_path = ""
        for n in arc.names:
            if n.lower().endswith(".opf"):
                opf_path = norm_key(n)
                break
    if not opf_path or opf_path not in by_key:
        return [], "OPF が見つかりません"

    opf_data = read_key(opf_path)
    opf = parse_xml(opf_data) if opf_data else None
    if opf is None:
        return [], "OPF をパースできません"
    opf_dir = dir_of(opf_path)

    # --- manifest (記述順) ---
    manifest_el = next((el for el in opf.iter() if local_name(el.tag) == "manifest"), None)
    if manifest_el is None:
        return [], "OPF に manifest がありません"
    by_id: Dict[str, dict] = {}
    manifest: List[dict] = []
    for el in manifest_el.iter():
        if local_name(el.tag) != "item" or not xml_attr(el, "href"):
            continue
        it = {
            "id": xml_attr(el, "id"),
            "path": resolve_href(opf_dir, xml_attr(el, "href")),
            "type": xml_attr(el, "media-type").lower(),
            "props": xml_attr(el, "properties"),
        }
        if not it["path"]:
            continue
        if it["id"]:
            by_id[it["id"]] = it
        manifest.append(it)
    if not manifest:
        return [], "OPF に manifest がありません"

    out: List[str] = []
    seen = set()

    def add(key: str) -> bool:
        if not key or key in seen or key not in img_keys:
            return False
        seen.add(key)
        out.append(img_keys[key])
        return True

    # --- metadata 優先モード: properties="cover-image" / <meta name="cover"> ---
    if cfg.get("epubCover") == "metadata":
        for it in manifest:
            if re.search(r"(^|\s)cover-image(\s|$)", it["props"]):
                add(it["path"])
                break
        if not out:
            for m in xml_find_all(opf, "meta"):
                if xml_attr(m, "name") == "cover":
                    ref = by_id.get(xml_attr(m, "content"))
                    if ref and add(ref["path"]):
                        break

    # --- spine を読み順に辿る ---
    spine_el = next((el for el in opf.iter() if local_name(el.tag) == "spine"), None)
    spine: List[dict] = []
    if spine_el is not None:
        for el in spine_el.iter():
            if local_name(el.tag) != "itemref":
                continue
            it = by_id.get(xml_attr(el, "idref"))
            if it:
                spine.append(it)

    for item in spine:
        if len(out) >= limit:
            break
        if item["type"].startswith("image/"):   # spine に画像が直接並ぶ EPUB (稀)
            add(item["path"])
            continue
        data = read_key(item["path"])
        if not data:
            continue
        for p in extract_doc_images(data, item["path"]):
            add(p)
            if len(out) >= limit:
                break

    note = None
    # --- spine から取れなければ manifest の image/* を記述順に ---
    if not out:
        for it in manifest:
            if it["type"].startswith("image/") and add(it["path"]) and len(out) >= limit:
                break
        note = "spine から画像を取れないため manifest 順を使用" if out else "読み順から画像を特定できません"
    return out, note


# ---------------------------------------------------------------- 画像 (Pillow)

def probe(data: bytes) -> Optional[Tuple[int, int]]:
    if Image is None:
        return None
    try:
        with Image.open(io.BytesIO(data)) as im:
            return im.size
    except Exception:
        return None


def pil_supports(fmt: str) -> bool:
    if pil_features is None:
        return False
    if fmt == "webp":
        return bool(pil_features.check("webp"))
    return True  # JPEG / PNG は Pillow に必ず入っている


def format_ext(fmt: str) -> str:
    return {"webp": "webp", "jpeg": "jpg", "png": "png"}.get(fmt, fmt)


def encode_image(im, cfg: dict, fmt: str) -> Tuple[bytes, int, int]:
    """最大解像度に収まるよう縮小して指定形式で書き出す (拡大はしない)"""
    try:
        im = ImageOps.exif_transpose(im) or im
    except Exception:
        pass

    max_w = cfg["maxWidth"] or 10 ** 9
    max_h = cfg["maxHeight"] or 10 ** 9
    if im.width > max_w or im.height > max_h:
        im.thumbnail((max_w, max_h), Image.LANCZOS)

    if fmt == "jpeg":
        if im.mode in ("RGBA", "LA", "P"):
            im = im.convert("RGBA")
            bg = Image.new("RGB", im.size, (255, 255, 255))
            bg.paste(im, mask=im.split()[-1])
            im = bg
        elif im.mode != "RGB":
            im = im.convert("RGB")
    elif fmt == "webp":
        if im.mode not in ("RGB", "RGBA"):
            im = im.convert("RGBA" if "A" in im.getbands() else "RGB")

    buf = io.BytesIO()
    if fmt == "png":
        im.save(buf, "PNG", optimize=True)
    else:
        im.save(buf, fmt.upper(), quality=int(cfg["quality"]))
    return buf.getvalue(), im.width, im.height


# ---------------------------------------------------------------- PDF

_PDF_PICKED: Optional[str] = None

PDF_ENGINES = ["pymupdf", "pypdfium2", "pdftoppm", "mutool", "magick", "gs"]


def pdf_first_page(path: str, cfg: dict) -> Tuple[Optional[object], str]:
    """PDF の 1 ページ目を PIL Image で返す。使えたエンジンは記憶して次回から直行する"""
    global _PDF_PICKED
    dpi = max(24, int(cfg["pdfDpi"]))

    if cfg["pdfEngine"] != "auto":
        engines = [cfg["pdfEngine"]]
    elif _PDF_PICKED:
        engines = [_PDF_PICKED]
    else:
        engines = PDF_ENGINES

    errs = []
    for eng in engines:
        im = None
        try:
            if eng == "pymupdf":
                try:
                    import pymupdf            # PyMuPDF 1.24.3+ のトップレベル名
                except ImportError:
                    import fitz as pymupdf    # 旧名 (import すると deprecation 警告が出る)

                with pymupdf.open(path) as doc:
                    if doc.page_count < 1:
                        raise ValueError("ページがありません")
                    pix = doc.load_page(0).get_pixmap(dpi=dpi)
                    im = Image.open(io.BytesIO(pix.tobytes("png")))
                    im.load()
            elif eng == "pypdfium2":
                import pypdfium2 as pdfium

                pdf = pdfium.PdfDocument(path)
                try:
                    if len(pdf) < 1:
                        raise ValueError("ページがありません")
                    im = pdf[0].render(scale=dpi / 72).to_pil()  # type: ignore[arg-type]
                    im.load()
                finally:
                    try:
                        pdf.close()
                    except Exception:
                        pass
            elif eng in ("pdftoppm", "mutool", "magick", "gs"):
                im = _pdf_via_command(eng, path, dpi, cfg, errs)
            else:
                errs.append("%s: 不明なエンジン" % eng)
                continue
        except ImportError:
            errs.append("%s: 未インストール (pip install %s)"
                        % (eng, "pymupdf" if eng == "pymupdf" else eng))
            continue
        except Exception as e:
            errs.append("%s: %s" % (eng, clip(str(e))))
            continue

        if im is not None:
            if cfg["pdfEngine"] == "auto":
                _PDF_PICKED = eng
            return im, ""
    return None, " / ".join(errs) if errs else "PDF をレンダリングできません"


def _pdf_via_command(eng: str, path: str, dpi: int, cfg: dict, errs: List[str]):
    """外部レンダラ。出力は必ず一時フォルダのファイルで受け取る。

    stdout 経由は移植性が無い — **Windows 版 pdftoppm 26.x は出力先の '-' を stdout ではなく
    ファイル名として扱い、カレントに '-.png' を作って stdout には何も出さない** (実測)。
    """
    binpath = which(eng, cfg)
    if not binpath:
        errs.append("%s: 見つかりません" % eng)
        return None

    import tempfile

    with tempfile.TemporaryDirectory(prefix="covimg_") as td:
        prefix = os.path.join(td, "page").replace("\\", "/")
        if eng == "pdftoppm":
            cmd = [binpath, "-png", "-r", str(dpi), "-f", "1", "-l", "1", "-singlefile", path, prefix]
        elif eng == "mutool":
            cmd = [binpath, "draw", "-F", "png", "-o", prefix + ".png", "-r", str(dpi), path, "1"]
        elif eng == "magick":
            cmd = [binpath, "-density", str(dpi), path + "[0]", "-background", "white", "-flatten", prefix + ".png"]
        else:  # gs
            cmd = [binpath, "-q", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=png16m",
                   "-dFirstPage=1", "-dLastPage=1", "-r%d" % dpi, "-sOutputFile=" + prefix + ".png", path]

        _, out, err = run(cmd)
        # -singlefile が効かない版に備えて prefix* も拾う
        hits = sorted(globmod.glob(prefix + "*.png"))
        if not hits:
            errs.append("%s: %s" % (eng, clip(err or out.decode('utf-8', 'replace')) or "画像が出力されません"))
            return None
        im = Image.open(hits[0])
        im.load()
        return im


# ---------------------------------------------------------------- 表紙の抽出

class Picked:
    def __init__(self, data: Optional[bytes], image, ext: Optional[str], src: str):
        self.data = data      # 書庫から取り出した原本バイト列 (passthrough 判定に使う)
        self.image = image    # PIL Image (PDF レンダリング結果)
        self.ext = ext        # 原本の拡張子 (PDF 由来なら None)
        self.src = src        # 表示用の出所


def extract_cover(full: str, ext: str, cfg: dict) -> Tuple[Optional[Picked], str]:
    if ext == "pdf":
        im, err = pdf_first_page(full, cfg)
        if im is None:
            return None, err
        return Picked(None, im, None, "p.1"), ""

    arc = open_archive(full, ext, cfg)
    if arc is None:
        need = "7z / unrar" if ext in RAR_EXTS else ("7z / py7zr" if ext in SEVEN_EXTS else "ZipArchive")
        return None, "書庫を開けません (%s が必要かもしれません)" % need

    try:
        by_name = dict(arc.entries)
        images = [n for n in arc.names if is_page_image(n)]
        if not images:
            return None, "画像ファイルが見つかりません"

        limit = max(1, int(cfg["maxCandidates"]))
        candidates: List[str] = []
        label = "ファイル名順(%s)" % cfg["sort"]

        if ext == "epub":
            candidates, note = epub_candidates(arc, images, cfg, limit)
            if candidates:
                label = "EPUB %s読み順" % ("metadata/" if cfg["epubCover"] == "metadata" else "")
                if note:
                    label += " (%s)" % note
            elif note:
                verbose("    EPUB 構造解析: %s → ファイル名順にフォールバック" % note)

        # 構造解析で取れなかった / EPUB 以外はファイル名順
        if cfg["sort"] == "natural":
            ordered = sorted(images, key=natural_key)
        else:
            ordered = sorted(images, key=lambda n: by_name.get(n, n.encode("utf-8", "replace")))
        for n in ordered:
            if len(candidates) >= limit * 2:
                break
            if n not in candidates:
                candidates.append(n)

        # 先頭から順に読んで、画像として認識できた最初の 1 枚を採用する
        tried = 0
        for n in candidates:
            if tried >= limit:
                break
            tried += 1
            data = arc.read(n)
            if not data:
                continue
            if probe(data) is None:
                verbose("    デコードできないので次の候補へ: " + n)
                continue
            return Picked(data, None, ext_of(n), "%s %s" % (label, n)), ""
        return None, "画像を読み出せません (%d 候補を試行)" % tried
    finally:
        arc.close()


# ---------------------------------------------------------------- 走査

def walk_targets(root: str, cfg: dict) -> List[str]:
    out: List[str] = []
    root = root.rstrip("/")
    exts = set(cfg["exts"])
    max_depth = int(cfg["maxDepth"])

    for dirpath, dirnames, filenames in os.walk(root, followlinks=bool(cfg["followSymlinks"])):
        rel = os.path.relpath(dirpath, root).replace("\\", "/")
        depth = 0 if rel == "." else rel.count("/") + 1
        if depth >= max_depth:
            dirnames[:] = []
        # ドットで始まるフォルダは library.php も見ないので降りない
        dirnames[:] = sorted(d for d in dirnames if not d.startswith("."))
        if not cfg["followSymlinks"]:
            dirnames[:] = [d for d in dirnames if not os.path.islink(os.path.join(dirpath, d))]
        for name in sorted(filenames):
            if name.startswith("."):
                continue
            if ext_of(name) in exts:
                out.append(os.path.join(dirpath, name).replace("\\", "/"))
    return out


# ---------------------------------------------------------------- CLI

def parse_args(argv: List[str]) -> argparse.Namespace:
    ap = argparse.ArgumentParser(
        prog="generate_coverimages.py",
        description="library.php 用の表紙画像 (サイドカー) を一括生成する",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="例:\n"
               "  python tools/generate_coverimages.py --root=D:/Books --check\n"
               "  python tools/generate_coverimages.py --root=D:/Books --dry-run -v\n"
               "  python tools/generate_coverimages.py --root=D:/Books --mtime\n",
    )
    g = ap.add_argument_group("対象の指定")
    g.add_argument("--root", help="表紙を作るフォルダ (必須。--config で指定してもよい)")
    g.add_argument("--config", help="設定 JSON のパス (library.config.php は読まない)")
    g.add_argument("--path", help="root 配下のサブフォルダだけを処理する")
    g.add_argument("--filter", help="相対パスにこの文字列を含むものだけ処理する (大小無視)")
    g.add_argument("--ext", help="対象拡張子を絞る (例: --ext=pdf,epub)")
    g.add_argument("--limit", type=int, default=0, help="最大 N 件だけ処理する")

    g = ap.add_argument_group("生成")
    g.add_argument("--format", choices=["webp", "jpeg", "jpg", "png"], help="出力形式 (既定: webp)")
    g.add_argument("--quality", type=int, help="webp / jpeg の品質 1-100 (既定: 82)")
    g.add_argument("--max-width", type=int, help="最大幅。超える画像だけ縮小。0 で無制限 (既定: 1200)")
    g.add_argument("--max-height", type=int, help="最大高さ (既定: 1600)")
    g.add_argument("--sort", choices=["lexical", "natural"], help="書庫内のファイル名順 (既定: lexical)")
    g.add_argument("--epub-cover", choices=["spine", "metadata"], help="EPUB の表紙の取り方 (既定: spine)")
    g.add_argument("--dpi", type=int, help="PDF のレンダリング解像度 (既定: 150)")
    g.add_argument("--pdf-engine", choices=["auto"] + PDF_ENGINES, help="PDF レンダラ (既定: auto)")
    g.add_argument("--passthrough", action="store_true", default=None,
                   help="縮小不要なら再エンコードせず原本をそのまま置く")

    g = ap.add_argument_group("上書き / 日時")
    g.add_argument("--force", action="store_true", help="既存の表紙があっても作り直す")
    g.add_argument("--stale", action="store_true", help="表紙が元ファイルより古いときだけ作り直す")
    g.add_argument("--mtime", action="store_true", default=None,
                   help="表紙の更新日時を抽出元ファイル (PDF/EPUB/書庫自体) に合わせる")
    g.add_argument("--no-mtime", dest="mtime", action="store_false",
                   help="設定で有効になっている --mtime を打ち消す")

    g = ap.add_argument_group("その他")
    g.add_argument("--dry-run", action="store_true", help="書き込まずに何をするか表示する")
    g.add_argument("--check", action="store_true", help="使えるバックエンドを表示して終了")
    g.add_argument("--console-encoding", default="", help="コンソール出力の文字コード (既定: 自動)")
    g.add_argument("-v", "--verbose", action="store_true")
    g.add_argument("-q", "--quiet", action="store_true")
    return ap.parse_args(argv)


def build_config(args: argparse.Namespace) -> dict:
    cfg = dict(DEFAULTS)
    cfg["commands"] = dict(DEFAULTS["commands"])

    if args.config:
        try:
            with open(args.config, "r", encoding="utf-8") as fh:
                user = json.load(fh)
        except Exception as e:
            die("設定ファイルを読めません: %s (%s)" % (args.config, e))
        if not isinstance(user, dict):
            die("設定ファイルは JSON オブジェクトにしてください: " + args.config)
        for k, v in user.items():
            if k == "commands" and isinstance(v, dict):
                cfg["commands"].update(v)
            else:
                cfg[k] = v

    # コマンドラインが最優先
    for opt, key in (("root", "root"), ("format", "format"), ("quality", "quality"),
                     ("max_width", "maxWidth"), ("max_height", "maxHeight"), ("sort", "sort"),
                     ("epub_cover", "epubCover"), ("dpi", "pdfDpi"), ("pdf_engine", "pdfEngine"),
                     ("passthrough", "passthrough"), ("mtime", "matchMtime")):
        val = getattr(args, opt, None)
        if val is not None:
            cfg[key] = val

    cfg["exts"] = [str(e).lower().lstrip(".") for e in cfg["exts"]]
    cfg["coverExts"] = [str(e).lower().lstrip(".") for e in cfg["coverExts"]]
    cfg["coverSuffix"] = re.sub(r"[/\\\0]", "", str(cfg["coverSuffix"]))
    if cfg["format"] == "jpg":
        cfg["format"] = "jpeg"
    for k in ("quality", "maxWidth", "maxHeight", "pdfDpi", "maxCandidates", "maxDepth"):
        cfg[k] = int(cfg[k])
    cfg["quality"] = max(1, min(100, cfg["quality"]))
    cfg["matchMtime"] = bool(cfg["matchMtime"])
    cfg["passthrough"] = bool(cfg["passthrough"])

    if args.ext:
        only = {e.strip().lstrip(".").lower() for e in args.ext.split(",") if e.strip()}
        cfg["exts"] = [e for e in cfg["exts"] if e in only]
        if not cfg["exts"]:
            die("--ext で指定した拡張子が対象 exts に含まれていません。")
    return cfg


def do_check(cfg: dict) -> None:
    print("Python         : %s (%s)" % (sys.version.split()[0], sys.platform))
    if Image is None:
        print("Pillow         : なし  ← 必須 (pip install pillow)")
    else:
        from PIL import __version__ as pilver
        print("Pillow         : %s (webp %s)" % (pilver, "可" if pil_supports("webp") else "不可"))
    for mod, label, hint in (("pymupdf", "PyMuPDF", "pip install pymupdf"),
                             ("pypdfium2", "pypdfium2", "pip install pypdfium2"),
                             ("py7zr", "py7zr", "pip install py7zr"),
                             ("rarfile", "rarfile", "pip install rarfile")):
        try:
            __import__(mod)
            print("%-15s: あり" % label)
        except Exception:
            print("%-15s: なし (%s)" % (label, hint))
    print("zipfile        : あり (標準ライブラリ / cbz・zip・epub)")
    for c in ("pdftoppm", "mutool", "magick", "gs", "7z", "unrar"):
        p = which(c, cfg)
        print("%-15s: %s" % (c, p or "なし"))


def main(argv: List[str]) -> int:
    global QUIET, VERBOSE
    args = parse_args(argv)
    QUIET, VERBOSE = args.quiet, args.verbose
    setup_console(args.console_encoding)

    cfg = build_config(args)

    if args.check:
        do_check(cfg)
        return 0

    if Image is None:
        die("Pillow が必要です: pip install pillow")

    out_fmt = cfg["format"]
    if not pil_supports(out_fmt):
        warn("注意: %s で書き出せないため jpeg を使います。" % out_fmt)
        out_fmt = "jpeg"
    out_ext = format_ext(out_fmt)
    if out_ext not in cfg["coverExts"]:
        die("出力拡張子 .%s が coverExts に含まれていません。library.php が表紙として認識できないので、"
            "--format を変えるか設定の coverExts に追加してください。" % out_ext)

    root = cfg.get("root") or ""
    if not root:
        die("--root を指定してください (または --config の JSON に \"root\" を書く)。")
    root = os.path.abspath(root).replace("\\", "/").rstrip("/")
    if not os.path.isdir(root):
        die("root フォルダが見つかりません: " + root)

    scan_root = root
    if args.path:
        sub = os.path.abspath(os.path.join(root, args.path)).replace("\\", "/").rstrip("/")
        if not os.path.isdir(sub):
            die("--path のフォルダが見つかりません: " + args.path)
        if sub != root and not sub.startswith(root + "/"):
            die("--path は root の外を指しています。")
        scan_root = sub

    files = walk_targets(scan_root, cfg)

    info("root      : " + root)
    info("対象       : %d 件 (%s)" % (len(files), ", ".join(cfg["exts"])))
    info("出力       : %s.%s / %s q%d / 最大 %sx%s / sort=%s / epub=%s%s%s" % (
        cfg["coverSuffix"], out_ext, out_fmt, cfg["quality"],
        cfg["maxWidth"] or "∞", cfg["maxHeight"] or "∞", cfg["sort"], cfg["epubCover"],
        " / mtime同期" if cfg["matchMtime"] else "", " / DRY-RUN" if args.dry_run else ""))
    info("")

    made = skipped = failed = 0
    started = time.time()
    index = 0

    for full in files:
        rel = full[len(root):].lstrip("/")
        if args.filter and args.filter.lower() not in rel.lower():
            continue
        if args.limit and made >= args.limit:
            break

        index += 1
        directory, name = os.path.split(full)
        ext = ext_of(name)
        try:
            src_mtime = os.path.getmtime(full)
        except OSError:
            src_mtime = 0.0

        # --- 既存の表紙 (どの拡張子でも) を探す ---
        existing = None
        for ce in cfg["coverExts"]:
            cand = os.path.join(directory, name + cfg["coverSuffix"] + "." + ce)
            if os.path.isfile(cand):
                existing = cand
                break
        if existing and not args.force:
            needs = args.stale and src_mtime and os.path.getmtime(existing) < src_mtime
            if not needs:
                # mtime 同期だけは既存ファイルにも当てておく (--mtime を後から付けたケース)
                if cfg["matchMtime"] and src_mtime and os.path.getmtime(existing) != src_mtime \
                        and not args.dry_run:
                    os.utime(existing, (src_mtime, src_mtime))
                    verbose("  [mtime] " + rel)
                skipped += 1
                verbose("  [skip] %s (%s)" % (rel, os.path.basename(existing)))
                continue

        picked, err = extract_cover(full, ext, cfg)
        if picked is None:
            failed += 1
            warn("  [NG]   %s — %s" % (rel, err or "不明なエラー"))
            continue

        # --- 縮小 / エンコード ---
        write_ext = out_ext
        data: Optional[bytes] = None
        dim = "?"
        if picked.image is not None:
            try:
                data, w, h = encode_image(picked.image, cfg, out_fmt)
                dim = "%dx%d" % (w, h)
            except Exception as e:
                failed += 1
                warn("  [NG]   %s — 画像を変換できません: %s" % (rel, clip(str(e))))
                continue
            finally:
                try:
                    picked.image.close()
                except Exception:
                    pass
        else:
            size = probe(picked.data or b"")
            need_resize = bool(size and ((cfg["maxWidth"] and size[0] > cfg["maxWidth"])
                                         or (cfg["maxHeight"] and size[1] > cfg["maxHeight"])))
            # passthrough: 縮小不要 & その拡張子が coverExts にある → 再エンコードしない
            if cfg["passthrough"] and not need_resize and picked.ext in cfg["coverExts"]:
                data = picked.data
                write_ext = picked.ext or out_ext
                dim = "%dx%d" % size if size else "?"
            else:
                try:
                    with Image.open(io.BytesIO(picked.data or b"")) as im:
                        im.load()
                        data, w, h = encode_image(im, cfg, out_fmt)
                    dim = "%dx%d" % (w, h)
                except Exception as e:
                    # デコードできない形式 (jxl/heic 等) でも、そのまま置ける形式なら原本を使う
                    if picked.ext in cfg["coverExts"] and not need_resize:
                        data = picked.data
                        write_ext = picked.ext or out_ext
                        dim = "%dx%d" % size if size else "?"
                        verbose("    再エンコード不可 (%s) → 原本をそのまま使用" % clip(str(e), 80))
                    else:
                        failed += 1
                        warn("  [NG]   %s — 画像を変換できません: %s" % (rel, clip(str(e))))
                        continue

        cover_name = name + cfg["coverSuffix"] + "." + write_ext
        cover_path = os.path.join(directory, cover_name)
        tag = "再生成" if existing else "生成"

        if args.dry_run:
            made += 1
            info("  [dry]  %s -> %s (%s, %s) %s" % (rel, cover_name, dim, human_size(len(data or b"")), picked.src))
            continue

        # 同じフォルダの一時ファイルに書いてから置き換える (途中で落ちても半端な表紙を残さない)。
        # '.' 始まりなので library.php の走査にも引っかからない。
        tmp = os.path.join(directory, ".covertmp_%d_%d" % (os.getpid(), index))
        try:
            with open(tmp, "wb") as fh:
                fh.write(data or b"")
            os.replace(tmp, cover_path)     # Windows でも上書きできる
        except OSError as e:
            failed += 1
            warn("  [NG]   %s — 書き込めません: %s" % (rel, clip(str(e))))
            try:
                os.unlink(tmp)
            except OSError:
                pass
            continue

        # 別拡張子の古い表紙が残っていると library.php が coverExts の優先順で拾ってしまうので消す
        for ce in cfg["coverExts"]:
            if ce == write_ext:
                continue
            old = os.path.join(directory, name + cfg["coverSuffix"] + "." + ce)
            if os.path.isfile(old):
                try:
                    os.unlink(old)
                    verbose("    古い表紙を削除: " + ce)
                except OSError:
                    pass

        # 表紙の更新日時を「抽出元ファイル自体」に合わせる (書庫内画像の日時ではない点に注意)
        if cfg["matchMtime"] and src_mtime:
            try:
                os.utime(cover_path, (src_mtime, src_mtime))
            except OSError:
                pass

        made += 1
        info("  [%s] %s -> %s (%s, %s)" % (tag, rel, cover_name, dim, human_size(len(data or b""))))
        verbose("    出所: " + picked.src)

    info("")
    info("完了: 生成 %d / スキップ %d / 失敗 %d  (%.1fs)%s"
         % (made, skipped, failed, time.time() - started, " ※DRY-RUN" if args.dry_run else ""))
    return 2 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
