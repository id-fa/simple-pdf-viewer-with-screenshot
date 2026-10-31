using System.Diagnostics;
using System.Text;
using LibraryServer.Core.Config;

namespace LibraryServer.Core.Index;

/// <summary>tree の 1 エントリ + 配信に必要な実パス</summary>
public sealed class LibraryEntry
{
    /// <summary>root 相対、'/' 区切り、NFC。クライアントに返す path</summary>
    public required string RelPath { get; init; }

    /// <summary>OS 上の実パス</summary>
    public required string FullPath { get; init; }

    public required string Name { get; init; }

    /// <summary>小文字・ドット無しの拡張子</summary>
    public required string Ext { get; init; }

    public long Size { get; init; }

    /// <summary>更新日時 (Unix 秒)</summary>
    public long Mtime { get; init; }

    /// <summary>走査時に見つかった表紙サイドカーの実パス</summary>
    public string? CoverFullPath { get; init; }

    public bool HasCover => CoverFullPath != null;

    public string Directory => Path.GetDirectoryName(FullPath)!;
}

/// <summary>ある時点の走査結果。不変。</summary>
public sealed class IndexSnapshot
{
    public required string RootReal { get; init; }
    public required IReadOnlyList<LibraryEntry> Entries { get; init; }
    public required IReadOnlyDictionary<string, LibraryEntry> ByKey { get; init; }
    public bool Truncated { get; init; }
    public int BadNames { get; init; }
    public int Unreadable { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public DateTimeOffset Generated { get; init; }
    public TimeSpan BuildDuration { get; init; }

    /// <summary>
    /// クライアントから来た path を引く。索引に無いものは何であれ null (存在しない / ドット始まり /
    /// 対象外拡張子 / 8.3 短縮名 / 代替データストリーム / デバイス名 … をまとめて弾く)。
    /// </summary>
    public LibraryEntry? Find(string? requestPath)
    {
        if (requestPath == null || !PathKeys.TryNormalize(requestPath, out var key)) return null;
        return ByKey.TryGetValue(key, out var e) ? e : null;
    }

    public static IndexSnapshot Empty(string rootReal) => new()
    {
        RootReal = rootReal,
        Entries = [],
        ByKey = new Dictionary<string, LibraryEntry>(),
        Warnings = [],
        Generated = DateTimeOffset.UtcNow,
    };
}

public static class PathKeys
{
    /// <summary>
    /// 要求パスを索引キーに正規化する: '\' → '/'、前後の '/' を落とす、NFC。
    /// NUL を含む / 空 / 不正なコードポイントは false。
    /// </summary>
    public static bool TryNormalize(string rel, out string key)
    {
        key = "";
        if (rel.Contains('\0')) return false;
        rel = rel.Replace('\\', '/').Trim('/');
        if (rel.Length == 0) return false;
        try
        {
            key = rel.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return false;
        }
        return true;
    }

    public static StringComparer KeyComparer => StringComparer.OrdinalIgnoreCase;
}

/// <summary>library.php の lib_walk 相当。root 以下を走査して IndexSnapshot を作る。</summary>
public static class LibraryIndexBuilder
{
    private sealed class WalkContext(LibraryConfig cfg, CancellationToken ct)
    {
        public readonly LibraryConfig Cfg = cfg;
        public readonly HashSet<string> ExtSet = cfg.ExtSet();
        public readonly CancellationToken Ct = ct;
        public readonly List<LibraryEntry> Entries = [];
        public readonly Dictionary<string, LibraryEntry> ByKey = new(PathKeys.KeyComparer);
        public readonly HashSet<string> Seen = new(StringComparer.OrdinalIgnoreCase);
        public bool Truncated;
        public int BadNames;
        public int Unreadable;
    }

    public static IndexSnapshot Build(LibraryConfig cfg, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (!cfg.IsConfigured) throw new InvalidOperationException("root が設定されていません / 'root' is not set");
        var rootInfo = new DirectoryInfo(cfg.Root);
        if (!rootInfo.Exists) throw new DirectoryNotFoundException($"root フォルダが見つかりません / The root folder was not found: {cfg.Root}");
        var rootReal = RealPath(rootInfo);

        var ctx = new WalkContext(cfg, ct);
        ctx.Seen.Add(rootReal);
        Walk(new DirectoryInfo(rootReal), "", 0, ctx);

        var warnings = new List<string>();
        if (ctx.BadNames > 0)
        {
            warnings.Add($"Unicode として解釈できないファイル名を {ctx.BadNames} 件除外しました。"
                + $" / Skipped {ctx.BadNames} file name(s) that are not valid Unicode.");
        }
        if (ctx.Unreadable > 0)
        {
            warnings.Add($"読み取れないフォルダが {ctx.Unreadable} 件ありました (権限を確認してください)。"
                + $" / {ctx.Unreadable} folder(s) could not be read (check permissions).");
        }
        if (ctx.Truncated)
        {
            warnings.Add($"件数が上限 ({cfg.MaxEntries}) に達したため一覧を打ち切りました。"
                + $" / The listing was truncated at the limit of {cfg.MaxEntries} entries.");
        }

        return new IndexSnapshot
        {
            RootReal = rootReal,
            Entries = ctx.Entries,
            ByKey = ctx.ByKey,
            Truncated = ctx.Truncated,
            BadNames = ctx.BadNames,
            Unreadable = ctx.Unreadable,
            Warnings = warnings,
            Generated = DateTimeOffset.UtcNow,
            BuildDuration = sw.Elapsed,
        };
    }

    private static void Walk(DirectoryInfo dir, string rel, int depth, WalkContext ctx)
    {
        var cfg = ctx.Cfg;
        ctx.Ct.ThrowIfCancellationRequested();
        if (depth > cfg.MaxDepth || ctx.Entries.Count >= cfg.MaxEntries) return;

        FileSystemInfo[] children;
        try
        {
            children = dir.GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            ctx.Unreadable++;
            return;
        }
        // scandir と同じく名前順 (バイト列順に近い ordinal)。クライアントは自前でソートするので厳密でなくてよい
        Array.Sort(children, static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        // 表紙サイドカーの有無を stat 無しで判定するための索引 (小文字名 → 実際の名前)
        var namesLower = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in children) namesLower.TryAdd(c.Name, c.Name);

        foreach (var child in children)
        {
            var name = child.Name;
            if (name.Length == 0 || name[0] == '.') continue;          // ドットファイル / フォルダは対象外
            if (ctx.Entries.Count >= cfg.MaxEntries) { ctx.Truncated = true; return; }

            // Windows ではシンボリックリンク・ジャンクション・OneDrive のプレースホルダ等が ReparsePoint。
            // library.php の is_link と同じく、followSymlinks でなければ辿らない
            var isLink = (child.Attributes & FileAttributes.ReparsePoint) != 0;
            if (!cfg.FollowSymlinks && isLink) continue;

            string utf8;
            try
            {
                utf8 = name.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                ctx.BadNames++;
                continue;
            }
            var childRel = rel.Length == 0 ? utf8 : rel + "/" + utf8;

            if (child is DirectoryInfo d)
            {
                string real;
                try { real = RealPath(d); }
                catch (Exception) { continue; }
                if (!ctx.Seen.Add(real)) continue;                       // リンクのループ対策
                Walk(d, childRel, depth + 1, ctx);
            }
            else if (child is FileInfo f)
            {
                var ext = ExtOf(name);
                if (!ctx.ExtSet.Contains(ext)) continue;
                long size = 0, mtime = 0;
                try { size = f.Length; mtime = ToUnixSeconds(f.LastWriteTimeUtc); } catch (IOException) { }
                var entry = new LibraryEntry
                {
                    RelPath = childRel,
                    FullPath = f.FullName,
                    Name = name,
                    Ext = ext,
                    Size = size,
                    Mtime = mtime,
                    CoverFullPath = FindCover(dir.FullName, name, cfg, namesLower),
                };
                ctx.Entries.Add(entry);
                ctx.ByKey.TryAdd(childRel, entry);                       // NFC で衝突したら先勝ち
            }
        }
    }

    /// <summary>
    /// 表紙サイドカー (元ファイル名 + coverSuffix + 画像拡張子) の実パス。無ければ null。
    /// namesLower (走査済みの名前一覧) があれば stat を撃たない。
    /// </summary>
    public static string? FindCover(string dir, string name, LibraryConfig cfg, IReadOnlyDictionary<string, string>? namesLower)
    {
        foreach (var ext in cfg.CoverExts)
        {
            var cand = name + cfg.CoverSuffix + "." + ext;
            if (namesLower != null)
            {
                if (namesLower.TryGetValue(cand, out var actual)) return Path.Combine(dir, actual);
            }
            else
            {
                var full = Path.Combine(dir, cand);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    public static string ExtOf(string name)
    {
        var pos = name.LastIndexOf('.');
        return pos < 0 ? "" : name[(pos + 1)..].ToLowerInvariant();
    }

    public static long ToUnixSeconds(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();

    /// <summary>realpath 相当。リンクなら最終的な実体のパス、それ以外は正規化した絶対パス</summary>
    public static string RealPath(FileSystemInfo info)
    {
        var full = Path.GetFullPath(info.FullName);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            try
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target != null) full = Path.GetFullPath(target.FullName);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return Path.TrimEndingDirectorySeparator(full);
    }
}
