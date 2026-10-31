using SkiaSharp;

namespace LibraryServer.Core.Covers;

/// <summary>SkiaSharp による表紙画像のデコード・縮小・書き出し (Python 版の Pillow 相当)</summary>
public static class ImageOps
{
    /// <summary>デコードせずに寸法だけ調べる。読めない形式 (AVIF / JXL / HEIC) は null</summary>
    public static (int Width, int Height)? Probe(byte[] data)
    {
        try
        {
            using var skData = SKData.CreateCopy(data);
            using var codec = SKCodec.Create(skData);
            if (codec == null) return null;
            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0) return null;
            // EXIF の回転で縦横が入れ替わる場合は表示上の寸法を返す
            return SwapsDimensions(codec.EncodedOrigin) ? (info.Height, info.Width) : (info.Width, info.Height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>EXIF の向きを正した SKBitmap。読めなければ null</summary>
    public static SKBitmap? Decode(byte[] data)
    {
        try
        {
            using var skData = SKData.CreateCopy(data);
            using var codec = SKCodec.Create(skData);
            if (codec == null) return null;
            var bmp = SKBitmap.Decode(codec);
            if (bmp == null) return null;
            var origin = codec.EncodedOrigin;
            if (origin == SKEncodedOrigin.TopLeft || origin == SKEncodedOrigin.Default) return bmp;
            using (bmp)
            {
                return ApplyOrigin(bmp, origin);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool SwapsDimensions(SKEncodedOrigin o) =>
        o is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>Pillow の ImageOps.exif_transpose 相当</summary>
    private static SKBitmap ApplyOrigin(SKBitmap src, SKEncodedOrigin origin)
    {
        int w = src.Width, h = src.Height;
        var swap = SwapsDimensions(origin);
        int dw = swap ? h : w, dh = swap ? w : h;
        var dst = new SKBitmap(new SKImageInfo(dw, dh, src.ColorType, src.AlphaType));
        using var canvas = new SKCanvas(dst);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:      // 2: 左右反転
                canvas.Translate(dw, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight:   // 3: 180°
                canvas.Translate(dw, dh); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft:    // 4: 上下反転
                canvas.Translate(0, dh); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop:       // 5: 転置 (90° 時計回り + 左右反転)
                canvas.Translate(dw, 0); canvas.Scale(-1, 1); canvas.Translate(dw, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightTop:      // 6: 90° 時計回り
                canvas.Translate(dw, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom:   // 7: 90° 時計回り + 上下反転
                canvas.Translate(0, dh); canvas.Scale(1, -1); canvas.Translate(dw, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.LeftBottom:    // 8: 90° 反時計回り
                canvas.Translate(0, dh); canvas.RotateDegrees(-90); break;
        }
        canvas.DrawBitmap(src, 0, 0, SKSamplingOptions.Default);
        canvas.Flush();
        return dst;
    }

    public sealed record Encoded(byte[] Data, int Width, int Height);

    /// <summary>
    /// 最大解像度に収まるよう縮小して指定形式で書き出す (拡大はしない)。
    /// JPEG は透過を白で埋める。format は webp | jpeg | png。
    /// </summary>
    public static Encoded Encode(SKBitmap src, string format, int quality, int maxW, int maxH)
    {
        var limitW = maxW > 0 ? maxW : int.MaxValue;
        var limitH = maxH > 0 ? maxH : int.MaxValue;
        SKBitmap work = src;
        SKBitmap? resized = null;
        try
        {
            if (src.Width > limitW || src.Height > limitH)
            {
                var scale = Math.Min(limitW / (double)src.Width, limitH / (double)src.Height);
                var nw = Math.Max(1, (int)Math.Floor(src.Width * scale));
                var nh = Math.Max(1, (int)Math.Floor(src.Height * scale));
                resized = src.Resize(new SKImageInfo(nw, nh, src.ColorType, src.AlphaType), new SKSamplingOptions(SKCubicResampler.Mitchell))
                          ?? throw new InvalidOperationException("縮小に失敗しました / resize failed");
                work = resized;
            }

            SKBitmap? flattened = null;
            try
            {
                if (format == "jpeg" && work.AlphaType != SKAlphaType.Opaque)
                {
                    flattened = new SKBitmap(new SKImageInfo(work.Width, work.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
                    using var c = new SKCanvas(flattened);
                    c.Clear(SKColors.White);
                    c.DrawBitmap(work, 0, 0, SKSamplingOptions.Default);
                    c.Flush();
                    work = flattened;
                }
                var fmt = format switch
                {
                    "jpeg" => SKEncodedImageFormat.Jpeg,
                    "png" => SKEncodedImageFormat.Png,
                    _ => SKEncodedImageFormat.Webp,
                };
                using var img = SKImage.FromBitmap(work);
                using var data = img.Encode(fmt, fmt == SKEncodedImageFormat.Png ? 100 : Math.Clamp(quality, 1, 100))
                                 ?? throw new InvalidOperationException($"{format} で書き出せません / cannot encode as {format}");
                return new Encoded(data.ToArray(), work.Width, work.Height);
            }
            finally
            {
                flattened?.Dispose();
            }
        }
        finally
        {
            resized?.Dispose();
        }
    }
}
