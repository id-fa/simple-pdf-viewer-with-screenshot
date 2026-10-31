using System.Runtime.Versioning;
using SkiaSharp;

namespace LibraryServer.Core.Covers;

/// <summary>PDF の 1 ページ目を PDFium (PDFtoImage) で描く。外部コマンドは使わない</summary>
[SupportedOSPlatform("windows")]
public static class PdfRenderer
{
    public static SKBitmap? RenderFirstPage(string path, int dpi, out string? error)
    {
        error = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var options = new PDFtoImage.RenderOptions(Dpi: Math.Clamp(dpi, 24, 600), BackgroundColor: SKColors.White);
            var bmp = PDFtoImage.Conversion.ToImage(fs, leaveOpen: true, password: null, page: 0, options: options);
            if (bmp == null) { error = "ページを描画できません / could not render the page"; return null; }
            return bmp;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }
}
