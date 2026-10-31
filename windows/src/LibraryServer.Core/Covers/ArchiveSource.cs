using System.Text;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace LibraryServer.Core.Covers;

/// <summary>書庫内の 1 エントリ。SortKey は PHP / Python 版と同じ「バイト列順」で並べるためのキー</summary>
public sealed class ArchiveEntryInfo
{
    public required string Name { get; init; }
    public required byte[] SortKey { get; init; }
    public required IArchiveEntry Entry { get; init; }
    public string BaseName => Name[(Name.LastIndexOf('/') + 1)..];
}

/// <summary>
/// cbz / zip / epub / cbr / rar / cb7 / 7z を SharpCompress で読む。形式はヘッダーから判定するので
/// 拡張子が偽装されていても (中身が rar の .cbz 等) 開ける。外部コマンド (7z / unrar) は使わない。
/// </summary>
public sealed class ArchiveSource : IDisposable
{
    // comic-viewer.html の IMAGE_EXTS と同じ
    private static readonly Regex ImageRe = new(@"\.(jpe?g|png|webp|gif|bmp|avif|jxl|tiff?|heic|heif)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IArchive _archive;
    private readonly Stream _stream;

    public string Kind { get; }
    public IReadOnlyList<ArchiveEntryInfo> Entries { get; }

    private ArchiveSource(IArchive archive, Stream stream, List<ArchiveEntryInfo> entries)
    {
        _archive = archive;
        _stream = stream;
        Entries = entries;
        Kind = archive.Type.ToString().ToLowerInvariant();
    }

    public static ArchiveSource? TryOpen(string path, out string? error)
    {
        error = null;
        Stream? fs = null;
        IArchive? arc = null;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            // UTF-8 フラグの無い ZIP のエントリ名は Latin1 で復号する: 1 バイト = 1 文字なので
            // 元のバイト列に戻せる (並べ替えキーに使う)。フラグ付きのエントリは SharpCompress が UTF-8 で読む
            var opts = new ReaderOptions
            {
                ArchiveEncoding = new ArchiveEncoding { Default = Encoding.Latin1 },
                LeaveStreamOpen = false,
            };
            arc = ArchiveFactory.OpenArchive(fs, opts);
            var list = new List<ArchiveEntryInfo>();
            foreach (var e in arc.Entries)
            {
                if (e.IsDirectory) continue;
                var name = (e.Key ?? "").Replace('\\', '/');
                if (name.Length == 0) continue;
                list.Add(new ArchiveEntryInfo { Name = name, SortKey = RawBytes(name), Entry = e });
            }
            if (list.Count == 0)
            {
                error = "書庫にファイルがありません / the archive has no files";
                arc.Dispose();
                return null;
            }
            return new ArchiveSource(arc, fs, list);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { arc?.Dispose(); } catch { }
            try { fs?.Dispose(); } catch { }
            return null;
        }
    }

    /// <summary>エントリを丸ごと読む。暗号化・破損なら null</summary>
    public byte[]? Read(ArchiveEntryInfo e)
    {
        try
        {
            if (e.Entry.IsEncrypted) return null;
            using var s = e.Entry.OpenEntryStream();
            using var ms = new MemoryStream(e.Entry.Size > 0 && e.Entry.Size < int.MaxValue ? (int)e.Entry.Size : 0);
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>ページ画像と見なすエントリ (macOS のリソースフォーク等は除外)</summary>
    public static bool IsPageImage(string name)
    {
        if (!ImageRe.IsMatch(name)) return false;
        if (name.Contains("__macosx/", StringComparison.OrdinalIgnoreCase)) return false;
        var b = name[(name.LastIndexOf('/') + 1)..];
        return !b.StartsWith("._", StringComparison.Ordinal) && !b.StartsWith('.');
    }

    public IEnumerable<ArchiveEntryInfo> PageImages() => Entries.Where(e => IsPageImage(e.Name));

    /// <summary>ファイル名を元のバイト列に戻す (Latin1 で復号したものはそのまま、UTF-8 名は UTF-8 で)</summary>
    private static byte[] RawBytes(string name)
    {
        foreach (var c in name)
        {
            if (c > 0xFF) return Encoding.UTF8.GetBytes(name);
        }
        return Encoding.Latin1.GetBytes(name);
    }

    public static int CompareSortKey(byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b);

    public void Dispose()
    {
        try { _archive.Dispose(); } catch { }
        try { _stream.Dispose(); } catch { }
    }
}

/// <summary>comic-viewer.html の naturalCompare / Python 版 natural_key と同じ規則の比較</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        x ??= ""; y ??= "";
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            var dx = char.IsAsciiDigit(x[i]);
            var dy = char.IsAsciiDigit(y[j]);
            if (dx && dy)
            {
                var si = i; while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                var sj = j; while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var c = a.SequenceCompareTo(b);
                if (c != 0) return c;
                continue;
            }
            if (dx != dy) return dx ? -1 : 1;              // 数字 < 非数字 (Python の (0, int) < (1, str))
            var ti = i; while (i < x.Length && !char.IsAsciiDigit(x[i])) i++;
            var tj = j; while (j < y.Length && !char.IsAsciiDigit(y[j])) j++;
            var cs = string.CompareOrdinal(x[ti..i], y[tj..j]);
            if (cs != 0) return cs;
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
