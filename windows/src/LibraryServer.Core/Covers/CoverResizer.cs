using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace LibraryServer.Core.Covers;

/// <summary>
/// action=cover の w= 指定で表紙を縮小して返す (library.php の lib_resize_image 相当)。
/// 元画像が w 以下なら null を返し、呼び出し側が原本を配信する。
/// 縮小結果はデータフォルダの covercache/ に ETag と同じキーで置き、2 回目以降は読むだけにする
/// (PHP 版には無い。毎回のデコード・縮小を避ける)。
/// </summary>
public sealed class CoverResizer
{
    public const long MaxPixels = 40_000_000;
    public const int Quality = 82;

    private readonly string? _cacheDir;
    private readonly ILogger _log;

    public CoverResizer(string? cacheDir, ILogger<CoverResizer> log)
    {
        _cacheDir = cacheDir;
        _log = log;
    }

    public static string ETagKey(string fullPath, long mtime, long size, int maxW)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"{fullPath}|{mtime}|{size}|{maxW}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public sealed record Resized(byte[] Data, string Mime);

    /// <summary>縮小が不要 / 不可能なら null</summary>
    public Resized? Resize(string fullPath, int maxW, string etagKey)
    {
        if (maxW <= 0) return null;

        SKImageInfo info;
        using (var codec = SKCodec.Create(fullPath))
        {
            if (codec == null) return null;                     // AVIF 等、Skia が読めない形式は原本を返す
            info = codec.Info;
        }
        if (info.Width <= 0 || info.Height <= 0) return null;
        if (info.Width <= maxW) return null;                    // 元がすでに小さい
        if ((long)info.Width * info.Height > MaxPixels) return null;

        var cachePath = _cacheDir == null ? null : Path.Combine(_cacheDir, etagKey + ".webp");
        if (cachePath != null && File.Exists(cachePath))
        {
            try { return new Resized(File.ReadAllBytes(cachePath), "image/webp"); }
            catch (IOException) { }
        }

        using var bmp = SKBitmap.Decode(fullPath);
        if (bmp == null) return null;
        var nh = Math.Max(1, (int)Math.Round(info.Height * (maxW / (double)info.Width)));
        using var dst = bmp.Resize(new SKImageInfo(maxW, nh, bmp.ColorType, bmp.AlphaType), new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (dst == null) return null;
        using var img = SKImage.FromBitmap(dst);
        using var data = img.Encode(SKEncodedImageFormat.Webp, Quality);
        if (data == null || data.Size == 0) return null;
        var bytes = data.ToArray();

        if (cachePath != null)
        {
            try
            {
                Directory.CreateDirectory(_cacheDir!);
                var tmp = cachePath + "." + Environment.ProcessId + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, cachePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "表紙キャッシュを書けません: {Path}", cachePath);
            }
        }
        return new Resized(bytes, "image/webp");
    }
}
