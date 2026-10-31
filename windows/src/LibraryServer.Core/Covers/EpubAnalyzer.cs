using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using LibraryServer.Core.Config;

namespace LibraryServer.Core.Covers;

/// <summary>
/// comic-viewer.html の analyzeEpub() / tools/generate_coverimages.py の epub_candidates() の移植。
/// container.xml → OPF → spine → XHTML の img / svg image / style url() を読み順に辿り、
/// 表紙候補 (書庫内の実エントリ) を数枚だけ返す。ページ順の全組み立てはしない。
/// </summary>
public static class EpubAnalyzer
{
    private static readonly Regex UrlRe = new(@"url\(\s*['""]?([^'"")]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SkipHrefRe = new(@"^(data:|https?:|blob:|#)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagRe = new(@"<([A-Za-z][\w:.-]*)((?:\s+[^\s=>/]+(?:\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+))?)*)\s*/?>", RegexOptions.Compiled);
    private static readonly Regex AttrRe = new(@"([^\s=>/]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.Compiled);
    private static readonly Regex CoverImagePropRe = new(@"(^|\s)cover-image(\s|$)", RegexOptions.Compiled);

    // ---------------------------------------------------------------- パス

    /// <summary>'..' / '.' / 先頭スラッシュを落とす (comic-viewer.html の sanitizePath と同じ)</summary>
    public static string SanitizePath(string name)
    {
        var parts = name.Replace('\\', '/').Split('/').Where(s => s is not ("" or "." or ".."));
        return string.Join('/', parts);
    }

    /// <summary>書庫エントリ名 ⇄ OPF/XHTML の href を突き合わせるための正規化キー</summary>
    public static string NormKey(string p)
    {
        try { p = Uri.UnescapeDataString(p); } catch (Exception) { }
        return SanitizePath(p).ToLowerInvariant();
    }

    public static string DirOf(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    /// <summary>href を基準ディレクトリから解決して正規化キーにする (fragment 除去 + ../ 解決)</summary>
    public static string ResolveHref(string baseDir, string href)
    {
        var h = (href ?? "").Replace('\\', '/');
        var cut = h.IndexOfAny(['#', '?']);
        if (cut >= 0) h = h[..cut];
        try { h = Uri.UnescapeDataString(h); } catch (Exception) { }
        if (h.Length == 0) return "";
        var parts = (h.StartsWith('/') || baseDir.Length == 0) ? new List<string>() : baseDir.Split('/').ToList();
        foreach (var seg in h.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(seg);
        }
        return string.Join('/', parts).ToLowerInvariant();
    }

    // ---------------------------------------------------------------- XML

    private static string LocalName(string tag)
    {
        var i = tag.LastIndexOf(':');
        return (i < 0 ? tag : tag[(i + 1)..]).ToLowerInvariant();
    }

    public static XDocument? ParseXml(byte[] data)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,   // XHTML の DOCTYPE で外部 DTD を取りに行かない
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                MaxCharactersFromEntities = 1024,
            };
            using var ms = new MemoryStream(data, writable: false);
            using var reader = XmlReader.Create(ms, settings);
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Attr(XElement el, string name)
    {
        foreach (var a in el.Attributes())
        {
            if (string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase)) return a.Value;
        }
        return "";
    }

    /// <summary>(タグ名, 属性) を文書順に返す。XML で読めなければ正規表現で拾い直す (壊れた XHTML 用)</summary>
    public static List<(string Tag, Dictionary<string, string> Attrs)> DocElements(byte[] data)
    {
        var doc = ParseXml(data);
        var outList = new List<(string, Dictionary<string, string>)>();
        if (doc?.Root != null)
        {
            foreach (var el in doc.Root.DescendantsAndSelf())
            {
                var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in el.Attributes()) attrs[a.Name.LocalName.ToLowerInvariant()] = a.Value;
                outList.Add((el.Name.LocalName.ToLowerInvariant(), attrs));
            }
            return outList;
        }
        var text = Encoding.UTF8.GetString(data);
        foreach (Match m in TagRe.Matches(text))
        {
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match am in AttrRe.Matches(m.Groups[2].Value))
            {
                var v = am.Groups[2].Success ? am.Groups[2].Value : am.Groups[3].Success ? am.Groups[3].Value : am.Groups[4].Value;
                attrs[LocalName(am.Groups[1].Value)] = System.Net.WebUtility.HtmlDecode(v);
            }
            outList.Add((LocalName(m.Groups[1].Value), attrs));
        }
        return outList;
    }

    /// <summary>XHTML / SVG 内の画像参照を文書順に返す (epubExtractDocImages 相当)。戻り値は正規化キー</summary>
    public static List<string> ExtractDocImages(byte[] data, string docPath)
    {
        var baseDir = DirOf(docPath);
        var result = new List<string>();
        void Push(string h)
        {
            if (string.IsNullOrEmpty(h) || SkipHrefRe.IsMatch(h)) return;
            var p = ResolveHref(baseDir, h);
            if (p.Length > 0) result.Add(p);
        }
        foreach (var (tag, attrs) in DocElements(data))
        {
            if (tag is "img" or "image")
            {
                var href = attrs.GetValueOrDefault("src") is { Length: > 0 } s ? s : attrs.GetValueOrDefault("href") ?? "";
                if (href.Length > 0) { Push(href); continue; }
            }
            // 固定レイアウト EPUB は背景画像で組まれていることがある
            var style = attrs.GetValueOrDefault("style") ?? "";
            if (style.Contains("url(", StringComparison.OrdinalIgnoreCase))
            {
                var m = UrlRe.Match(style);
                if (m.Success) Push(m.Groups[1].Value);
            }
        }
        return result;
    }

    // ---------------------------------------------------------------- 候補

    public sealed record Result(List<ArchiveEntryInfo> Candidates, string? Note);

    /// <summary>EPUB の読み順から表紙候補を最大 limit 件返す</summary>
    public static Result Candidates(ArchiveSource arc, IReadOnlyList<ArchiveEntryInfo> images, CoverConfig cfg, int limit)
    {
        var byKey = new Dictionary<string, ArchiveEntryInfo>();
        foreach (var e in arc.Entries) byKey.TryAdd(NormKey(e.Name), e);
        var imgKeys = new Dictionary<string, ArchiveEntryInfo>();
        foreach (var e in images) imgKeys.TryAdd(NormKey(e.Name), e);

        byte[]? ReadKey(string key) => byKey.TryGetValue(key, out var e) ? arc.Read(e) : null;

        // --- OPF を特定 ---
        var opfPath = "";
        var container = ReadKey("meta-inf/container.xml");
        if (container != null)
        {
            var root = ParseXml(container)?.Root;
            if (root != null)
            {
                foreach (var rf in root.DescendantsAndSelf().Where(e => e.Name.LocalName.Equals("rootfile", StringComparison.OrdinalIgnoreCase)))
                {
                    var fp = Attr(rf, "full-path");
                    if (fp.Length > 0) { opfPath = ResolveHref("", fp); break; }
                }
            }
        }
        if (!byKey.ContainsKey(opfPath))
        {
            opfPath = "";
            foreach (var e in arc.Entries)
            {
                if (e.Name.EndsWith(".opf", StringComparison.OrdinalIgnoreCase)) { opfPath = NormKey(e.Name); break; }
            }
        }
        if (opfPath.Length == 0 || !byKey.ContainsKey(opfPath)) return new Result([], "OPF が見つかりません");

        var opfData = ReadKey(opfPath);
        var opf = opfData == null ? null : ParseXml(opfData)?.Root;
        if (opf == null) return new Result([], "OPF をパースできません");
        var opfDir = DirOf(opfPath);

        // --- manifest (記述順) ---
        var manifestEl = opf.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName.Equals("manifest", StringComparison.OrdinalIgnoreCase));
        if (manifestEl == null) return new Result([], "OPF に manifest がありません");
        var byId = new Dictionary<string, ManifestItem>();
        var manifest = new List<ManifestItem>();
        foreach (var el in manifestEl.Descendants())
        {
            if (!el.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase)) continue;
            var href = Attr(el, "href");
            if (href.Length == 0) continue;
            var it = new ManifestItem(Attr(el, "id"), ResolveHref(opfDir, href), Attr(el, "media-type").ToLowerInvariant(), Attr(el, "properties"));
            if (it.Path.Length == 0) continue;
            if (it.Id.Length > 0) byId.TryAdd(it.Id, it);
            manifest.Add(it);
        }
        if (manifest.Count == 0) return new Result([], "OPF に manifest がありません");

        var result = new List<ArchiveEntryInfo>();
        var seen = new HashSet<string>();
        bool Add(string key)
        {
            if (key.Length == 0 || seen.Contains(key) || !imgKeys.TryGetValue(key, out var e)) return false;
            seen.Add(key);
            result.Add(e);
            return true;
        }

        // --- metadata 優先モード: properties="cover-image" / <meta name="cover"> ---
        if (cfg.EpubCover == "metadata")
        {
            foreach (var it in manifest)
            {
                if (CoverImagePropRe.IsMatch(it.Props)) { Add(it.Path); break; }
            }
            if (result.Count == 0)
            {
                foreach (var m in opf.DescendantsAndSelf().Where(e => e.Name.LocalName.Equals("meta", StringComparison.OrdinalIgnoreCase)))
                {
                    if (Attr(m, "name") == "cover" && byId.TryGetValue(Attr(m, "content"), out var r) && Add(r.Path)) break;
                }
            }
        }

        // --- spine を読み順に辿る ---
        var spineEl = opf.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName.Equals("spine", StringComparison.OrdinalIgnoreCase));
        var spine = new List<ManifestItem>();
        if (spineEl != null)
        {
            foreach (var el in spineEl.Descendants())
            {
                if (!el.Name.LocalName.Equals("itemref", StringComparison.OrdinalIgnoreCase)) continue;
                if (byId.TryGetValue(Attr(el, "idref"), out var it)) spine.Add(it);
            }
        }
        foreach (var item in spine)
        {
            if (result.Count >= limit) break;
            if (item.Type.StartsWith("image/", StringComparison.Ordinal)) { Add(item.Path); continue; }   // spine に画像が直接並ぶ EPUB (稀)
            var data = ReadKey(item.Path);
            if (data == null) continue;
            foreach (var p in ExtractDocImages(data, item.Path))
            {
                Add(p);
                if (result.Count >= limit) break;
            }
        }

        string? note = null;
        // --- spine から取れなければ manifest の image/* を記述順に ---
        if (result.Count == 0)
        {
            foreach (var it in manifest)
            {
                if (it.Type.StartsWith("image/", StringComparison.Ordinal) && Add(it.Path) && result.Count >= limit) break;
            }
            note = result.Count > 0 ? "spine から画像を取れないため manifest 順を使用" : "読み順から画像を特定できません";
        }
        return new Result(result, note);
    }

    private sealed record ManifestItem(string Id, string Path, string Type, string Props);
}
