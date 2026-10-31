using System.IO.Compression;
using System.Text;
using LibraryServer.Core.Config;
using LibraryServer.Core.Covers;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace LibraryServer.Tests;

public static class Fixtures
{
    public static byte[] Png(int w, int h, SKColor color)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) c.Clear(color);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static byte[] Jpeg(int w, int h, SKColor color)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) c.Clear(color);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    /// <summary>ZIP を組み立てる (エントリ順は与えた順)</summary>
    public static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Fastest);
                using var s = e.Open();
                s.Write(data);
            }
        }
        return ms.ToArray();
    }

    /// <summary>1 ページだけの最小 PDF (xref 無し。PDFium が再構築する)。赤い矩形が描かれる</summary>
    public static byte[] MinimalPdf(int w = 200, int h = 300)
    {
        var content = $"1 0 0 rg 0 0 {w} {h} re f";
        var pdf = "%PDF-1.4\n" +
                  "1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n" +
                  "2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n" +
                  $"3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Contents 4 0 R >> endobj\n" +
                  $"4 0 obj << /Length {content.Length} >> stream\n{content}\nendstream endobj\n" +
                  "trailer << /Root 1 0 R >>\n%%EOF\n";
        return Encoding.ASCII.GetBytes(pdf);
    }

    public static byte[] Epub(string imageFirstInSpine, params (string Name, byte[] Data)[] images)
    {
        var container = """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""";
        var manifestItems = string.Join("", images.Select((im, i) => $"""<item id="img{i}" href="images/{im.Name}" media-type="image/png"{(i == 0 ? " properties=\"cover-image\"" : "")}/>"""));
        var opf = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>t</dc:title><meta name="cover" content="img0"/></metadata>
              <manifest>
                {manifestItems}
                <item id="p1" href="text/p1.xhtml" media-type="application/xhtml+xml"/>
                <item id="p2" href="text/p2.xhtml" media-type="application/xhtml+xml"/>
              </manifest>
              <spine><itemref idref="p1"/><itemref idref="p2"/></spine>
            </package>
            """;
        var p1 = $"""<!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.1//EN" "http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd"><html xmlns="http://www.w3.org/1999/xhtml"><body><div><img src="../images/{imageFirstInSpine}"/></div></body></html>""";
        var p2 = """<html xmlns="http://www.w3.org/1999/xhtml"><body><svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"><image xlink:href="../images/zzz.png"/></svg></body></html>""";
        var entries = new List<(string, byte[])>
        {
            ("mimetype", Encoding.ASCII.GetBytes("application/epub+zip")),
            ("META-INF/container.xml", Encoding.UTF8.GetBytes(container)),
            ("OEBPS/content.opf", Encoding.UTF8.GetBytes(opf)),
            ("OEBPS/text/p1.xhtml", Encoding.UTF8.GetBytes(p1)),
            ("OEBPS/text/p2.xhtml", Encoding.UTF8.GetBytes(p2)),
        };
        foreach (var (n, d) in images) entries.Add(("OEBPS/images/" + n, d));
        return Zip(entries.ToArray());
    }
}

public class NaturalComparerTests
{
    [Fact]
    public void Numeric_runs_compare_as_numbers()
    {
        var names = new[] { "img_10.png", "img_2.png", "img_100.png", "img_1.png" };
        Assert.Equal(["img_1.png", "img_2.png", "img_10.png", "img_100.png"], names.OrderBy(n => n, NaturalComparer.Instance));
        Assert.Equal(["img_1.png", "img_10.png", "img_100.png", "img_2.png"], names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Digits_sort_before_letters_like_python_tuple_key()
    {
        Assert.True(NaturalComparer.Instance.Compare("1.png", "a.png") < 0);
        Assert.True(NaturalComparer.Instance.Compare("a01", "a1") == 0 || NaturalComparer.Instance.Compare("a01", "a1") < 0);
    }
}

public class EpubAnalyzerPathTests
{
    [Theory]
    [InlineData("OEBPS/text", "../images/a.png", "oebps/images/a.png")]
    [InlineData("OEBPS/text", "b.xhtml#frag", "oebps/text/b.xhtml")]
    [InlineData("", "/abs/x.png", "abs/x.png")]
    [InlineData("a", "%E7%94%BB.png", "a/画.png")]
    public void Resolves_hrefs(string baseDir, string href, string expected)
    {
        Assert.Equal(expected, EpubAnalyzer.ResolveHref(baseDir, href));
    }

    [Fact]
    public void Extracts_images_from_broken_xhtml_via_regex_fallback()
    {
        var html = "<html><body><p>unclosed<img src=\"../img/1.jpg\"><div style=\"background:url('../img/2.jpg')\"></body>"u8.ToArray();
        Assert.Equal(["oebps/img/1.jpg", "oebps/img/2.jpg"], EpubAnalyzer.ExtractDocImages(html, "OEBPS/text/p.xhtml"));
    }
}

public class CoverGeneratorTests
{
    private static CoverGenerator Gen(TempLibrary lib, Action<LibraryConfig>? mutate = null) =>
        new(lib.Config(mutate), NullLogger.Instance);

    private static string CoverPath(TempLibrary lib, string rel, string ext = "webp") =>
        Path.Combine(lib.Root, rel.Replace('/', Path.DirectorySeparatorChar) + ".coverimage." + ext);

    private static (int W, int H) Dims(string path)
    {
        using var codec = SKCodec.Create(path);
        return (codec.Info.Width, codec.Info.Height);
    }

    [Fact]
    public void Cbz_picks_first_image_by_byte_order_and_skips_macosx()
    {
        using var lib = new TempLibrary();
        lib.Write("v.cbz", Fixtures.Zip(
            ("__MACOSX/._001.png", Fixtures.Png(10, 10, SKColors.Black)),
            ("002.png", Fixtures.Png(30, 40, SKColors.Blue)),
            ("001.png", Fixtures.Png(50, 60, SKColors.Red)),
            ("readme.txt", "x"u8.ToArray())));
        var summary = Gen(lib).Run(new CoverJob(), null, CancellationToken.None);
        Assert.Equal((1, 0, 0), (summary.Made, summary.Skipped, summary.Failed));
        var cover = CoverPath(lib, "v.cbz");
        Assert.True(File.Exists(cover));
        Assert.Equal((50, 60), Dims(cover));
    }

    [Fact]
    public void Natural_sort_changes_the_pick()
    {
        using var lib = new TempLibrary();
        lib.Write("v.cbz", Fixtures.Zip(
            ("img_10.png", Fixtures.Png(10, 10, SKColors.Black)),
            ("img_2.png", Fixtures.Png(20, 20, SKColors.Blue))));
        Gen(lib).Run(new CoverJob(), null, CancellationToken.None);
        Assert.Equal((10, 10), Dims(CoverPath(lib, "v.cbz")));

        Gen(lib, c => c.Covers.Sort = "natural").Run(new CoverJob { Force = true }, null, CancellationToken.None);
        Assert.Equal((20, 20), Dims(CoverPath(lib, "v.cbz")));
    }

    [Fact]
    public void Undecodable_first_candidate_falls_through_to_next()
    {
        using var lib = new TempLibrary();
        lib.Write("v.cbz", Fixtures.Zip(
            ("000.jpg", "not an image"u8.ToArray()),
            ("001.png", Fixtures.Png(12, 12, SKColors.Red))));
        var s = Gen(lib).Run(new CoverJob(), null, CancellationToken.None);
        Assert.Equal(1, s.Made);
        Assert.Equal((12, 12), Dims(CoverPath(lib, "v.cbz")));
    }

    [Fact]
    public void Resizes_to_fit_max_box_without_upscaling()
    {
        using var lib = new TempLibrary();
        lib.Write("big.cbz", Fixtures.Zip(("a.png", Fixtures.Png(2000, 3000, SKColors.Green))));
        lib.Write("small.cbz", Fixtures.Zip(("a.png", Fixtures.Png(100, 150, SKColors.Green))));
        Gen(lib).Run(new CoverJob(), null, CancellationToken.None);
        Assert.Equal((1066, 1600), Dims(CoverPath(lib, "big.cbz")));
        Assert.Equal((100, 150), Dims(CoverPath(lib, "small.cbz")));
    }

    [Fact]
    public void Pdf_first_page_is_rendered()
    {
        using var lib = new TempLibrary();
        lib.Write("doc.pdf", Fixtures.MinimalPdf(200, 300));
        var events = new List<CoverEvent>();
        var s = Gen(lib, c => c.Covers.PdfDpi = 72).Run(new CoverJob(), new Collector(events), CancellationToken.None);
        Assert.True(s.Failed == 0, string.Join("; ", events.Select(e => e.Message)));
        var cover = CoverPath(lib, "doc.pdf");
        Assert.True(File.Exists(cover));
        var (w, h) = Dims(cover);
        Assert.InRange(w, 190, 210);
        Assert.InRange(h, 290, 310);
        using var bmp = SKBitmap.Decode(cover);
        var px = bmp.GetPixel(w / 2, h / 2);
        Assert.True(px.Red > 200 && px.Green < 60 && px.Blue < 60, $"center pixel {px}");
    }

    [Fact]
    public void Epub_uses_reading_order_then_metadata_when_asked()
    {
        using var lib = new TempLibrary();
        lib.Write("book.epub", Fixtures.Epub("second.png",
            ("first.png", Fixtures.Png(11, 11, SKColors.Red)),       // manifest 先頭 + cover-image
            ("second.png", Fixtures.Png(22, 22, SKColors.Blue)),     // spine の 1 ページ目が参照
            ("aaa.png", Fixtures.Png(33, 33, SKColors.Black))));     // ファイル名順なら先頭

        var ev = new List<CoverEvent>();
        Gen(lib).Run(new CoverJob(), new Collector(ev), CancellationToken.None);
        Assert.Equal((22, 22), Dims(CoverPath(lib, "book.epub")));
        Assert.Contains("EPUB 読み順", ev.Single().Source);

        Gen(lib, c => c.Covers.EpubCover = "metadata").Run(new CoverJob { Force = true }, null, CancellationToken.None);
        Assert.Equal((11, 11), Dims(CoverPath(lib, "book.epub")));
    }

    [Fact]
    public void Skip_stale_force_and_mtime_rules()
    {
        using var lib = new TempLibrary();
        var src = lib.Write("v.cbz", Fixtures.Zip(("a.png", Fixtures.Png(10, 10, SKColors.Red))), new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var cover = CoverPath(lib, "v.cbz");

        var s1 = Gen(lib).Run(new CoverJob(), null, CancellationToken.None);
        Assert.Equal(1, s1.Made);
        var s2 = Gen(lib).Run(new CoverJob(), null, CancellationToken.None);
        Assert.Equal((0, 1), (s2.Made, s2.Skipped));

        // --stale: 表紙が元ファイルより古いときだけ
        File.SetLastWriteTimeUtc(cover, new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var s3 = Gen(lib).Run(new CoverJob { Stale = true }, null, CancellationToken.None);
        Assert.Equal(1, s3.Made);
        Assert.True(File.GetLastWriteTimeUtc(cover) > new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        // matchMtime: スキップされる既存ファイルにも当たる
        Gen(lib, c => c.Covers.MatchMtime = true).Run(new CoverJob(), null, CancellationToken.None);
        Assert.Equal(File.GetLastWriteTimeUtc(src), File.GetLastWriteTimeUtc(cover));

        // --force で作り直し、別拡張子の古い表紙は消える
        File.WriteAllBytes(CoverPath(lib, "v.cbz", "png"), TempLibrary.TinyPng);
        var s4 = Gen(lib).Run(new CoverJob { Force = true }, null, CancellationToken.None);
        Assert.Equal(1, s4.Made);
        Assert.False(File.Exists(CoverPath(lib, "v.cbz", "png")));
        Assert.True(File.Exists(cover));
    }

    [Fact]
    public void Dry_run_writes_nothing_and_filters_apply()
    {
        using var lib = new TempLibrary();
        lib.Write("A/one.cbz", Fixtures.Zip(("a.png", Fixtures.Png(10, 10, SKColors.Red))));
        lib.Write("B/two.cbz", Fixtures.Zip(("a.png", Fixtures.Png(10, 10, SKColors.Red))));
        lib.Write("B/three.pdf", Fixtures.MinimalPdf());
        var ev = new List<CoverEvent>();
        var s = Gen(lib).Run(new CoverJob { DryRun = true, SubPath = "B", OnlyExts = ["cbz"] }, new Collector(ev), CancellationToken.None);
        Assert.Equal(1, s.Made);
        Assert.Equal("B/two.cbz", ev.Single().RelPath);
        Assert.Equal(CoverOutcome.DryRun, ev.Single().Outcome);
        Assert.Empty(Directory.GetFiles(lib.Root, "*.coverimage.*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Jpeg_output_and_passthrough()
    {
        using var lib = new TempLibrary();
        var jpg = Fixtures.Jpeg(40, 40, SKColors.Gray);
        lib.Write("v.cbz", Fixtures.Zip(("a.jpg", jpg)));
        Gen(lib, c => { c.Covers.Format = "jpeg"; c.Covers.Passthrough = true; }).Run(new CoverJob(), null, CancellationToken.None);
        var cover = CoverPath(lib, "v.cbz", "jpg");
        Assert.True(File.Exists(cover));
        Assert.Equal(jpg, File.ReadAllBytes(cover));                 // 再エンコードせず原本
    }

    [Fact]
    public void Validate_rejects_output_ext_missing_from_coverExts()
    {
        using var lib = new TempLibrary();
        var gen = Gen(lib, c => { c.CoverExts = ["png"]; c.Covers.Format = "webp"; });
        Assert.NotNull(gen.Validate());
    }

    private sealed class Collector(List<CoverEvent> list) : IProgress<CoverEvent>
    {
        public void Report(CoverEvent value) => list.Add(value);
    }
}
