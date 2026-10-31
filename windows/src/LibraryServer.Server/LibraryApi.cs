using System.Text.Json;
using LibraryServer.Core;
using LibraryServer.Core.Auth;
using LibraryServer.Core.Covers;
using LibraryServer.Core.Index;

namespace LibraryServer.Server;

/// <summary>
/// GET /library.php?action=ping|tree|file|cover — PHP 版 library.php と同じレスポンスを返す。
/// ルートを library.php のままにしてあるのは、comic-viewer.html / pdf-viewer.html の
/// LIBRARY_API 定数と sw.js の素通し判定 (endsWith('/library.php')) を無改修で使うため。
/// </summary>
public static class LibraryApi
{
    public const string Route = "/library.php";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Map(WebApplication app)
    {
        app.MapMethods(Route, ["GET", "HEAD"], Handle);
    }

    private static async Task Handle(HttpContext ctx, LibraryRuntime rt)
    {
        var cfg = rt.Config;

        // PHP 版は library.config.php が無いと 503。クライアントはこれを「未設置」と判定して UI を隠すので揃える
        if (!cfg.IsConfigured)
        {
            await Fail(ctx, 503, "root が設定されていません。設定画面 (トレイ → 設定) か library.config.json の root に公開フォルダを書いてください。"
                + " / 'root' is not configured. Set the library folder in the settings window or in library.config.json.");
            return;
        }

        // 設定の読み込み直後・ファイルに触れる前に認証する
        if (BasicAuth.IsEnabled(cfg.Auth) && !BasicAuth.Verify(cfg.Auth!, ctx.Request.Headers.Authorization))
        {
            ctx.Response.Headers.WWWAuthenticate = BasicAuth.RealmHeader(cfg.Auth!);
            await Fail(ctx, 401, "ライブラリの閲覧にはログインが必要です / Sign in to browse the library");
            return;
        }

        if (!Directory.Exists(cfg.Root))
        {
            await Fail(ctx, 500, "root フォルダが見つかりません / The root folder was not found: " + cfg.Root);
            return;
        }

        var action = ctx.Request.Query["action"].ToString();
        switch (action)
        {
            case "ping":
                await WriteJson(ctx, 200, new { ok = true, root = cfg.DisplayLabel(), exts = cfg.Exts });
                return;
            case "tree":
                await Tree(ctx, rt);
                return;
            case "file":
                await SendFile(ctx, rt);
                return;
            case "cover":
                await SendCover(ctx, rt);
                return;
            default:
                await Fail(ctx, 400, "不明な action です / Unknown action (ping / tree / file / cover)");
                return;
        }
    }

    // ------------------------------------------------------------ tree

    private static async Task Tree(HttpContext ctx, LibraryRuntime rt)
    {
        IndexSnapshot snap;
        try
        {
            snap = await rt.Index.GetAsync(ctx.RequestAborted);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            await Fail(ctx, 500, "一覧の取得に失敗しました / Failed to read the library: " + ex.Message);
            return;
        }

        var res = ctx.Response;
        res.StatusCode = 200;
        res.ContentType = "application/json; charset=utf-8";
        res.Headers.CacheControl = "no-store";
        res.Headers.XContentTypeOptions = "nosniff";

        // 5000 件でも一発で書けるように Utf8JsonWriter で直接組む ({path,size,mtime,cover?} — PHP 版と同じ形)
        await using (var w = new Utf8JsonWriter(res.BodyWriter, new JsonWriterOptions { Encoder = Json.Encoder, SkipValidation = true }))
        {
            w.WriteStartObject();
            w.WriteString("root", rt.Config.DisplayLabel());
            w.WriteNumber("generated", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            w.WriteBoolean("truncated", snap.Truncated);
            w.WriteStartArray("warnings");
            foreach (var s in snap.Warnings) w.WriteStringValue(s);
            w.WriteEndArray();
            w.WriteStartArray("entries");
            foreach (var e in snap.Entries)
            {
                w.WriteStartObject();
                w.WriteString("path", e.RelPath);
                w.WriteNumber("size", e.Size);
                w.WriteNumber("mtime", e.Mtime);
                if (e.HasCover) w.WriteBoolean("cover", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            await w.FlushAsync(ctx.RequestAborted);
        }
        await res.BodyWriter.FlushAsync(ctx.RequestAborted);
    }

    // ------------------------------------------------------------ path

    /// <summary>
    /// ?path= を索引から引く。library.php の lib_request_path に相当するが、文字列からファイルを
    /// 開くことは一切せず「走査で見つかったものだけ」を返す。これで '..' / 絶対パス / ドット始まり /
    /// 対象外拡張子に加え、Windows 固有の 8.3 短縮名・代替データストリーム・予約デバイス名も
    /// まとめて 404 になる。
    /// </summary>
    private static async Task<LibraryEntry?> ResolveEntry(HttpContext ctx, LibraryRuntime rt)
    {
        var rel = ctx.Request.Query["path"].ToString();
        if (string.IsNullOrEmpty(rel))
        {
            await Fail(ctx, 400, "path が指定されていません / No path was given");
            return null;
        }
        IndexSnapshot snap;
        try { snap = await rt.Index.GetAsync(ctx.RequestAborted); }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            await Fail(ctx, 500, "一覧の取得に失敗しました / Failed to read the library: " + ex.Message);
            return null;
        }
        var entry = snap.Find(rel);
        if (entry == null || !File.Exists(entry.FullPath))
        {
            if (entry != null) rt.Index.MarkDirty();           // 索引が古い (消された) → 作り直す
            await Fail(ctx, 404, "ファイルが見つかりません / File not found");
            return null;
        }
        return entry;
    }

    // ------------------------------------------------------------ file

    private static async Task SendFile(HttpContext ctx, LibraryRuntime rt)
    {
        var entry = await ResolveEntry(ctx, rt);
        if (entry == null) return;

        var res = ctx.Response;
        // 数百MBのアーカイブをブラウザキャッシュに溜めても得がないので保存しない
        res.Headers.CacheControl = "no-store";
        res.Headers.XContentTypeOptions = "nosniff";
        res.Headers.ContentDisposition = "inline; filename*=UTF-8''" + Uri.EscapeDataString(entry.Name);

        // Range (206 / 416 / If-Range) は ASP.NET Core の実装に任せる。Accept-Ranges / Content-Length も付く
        var result = Results.File(entry.FullPath, Mime.ForBookExt(entry.Ext), enableRangeProcessing: true);
        await result.ExecuteAsync(ctx);
    }

    // ------------------------------------------------------------ cover

    private static async Task SendCover(HttpContext ctx, LibraryRuntime rt)
    {
        var entry = await ResolveEntry(ctx, rt);
        if (entry == null) return;

        // path は「本のパス」。表紙のファイル名はサーバー側で導出する (画像パスを受け取らない)
        var cover = entry.CoverFullPath;
        if (cover == null || !File.Exists(cover))
        {
            cover = LibraryIndexBuilder.FindCover(entry.Directory, entry.Name, rt.Config, null);
        }
        if (cover == null)
        {
            await Fail(ctx, 404, "表紙画像がありません / No cover image for this file");
            return;
        }

        var maxW = 0;
        if (int.TryParse(ctx.Request.Query["w"], out var w) && w > 0) maxW = Math.Clamp(w, 64, 2000);

        var fi = new FileInfo(cover);
        var mtime = LibraryIndexBuilder.ToUnixSeconds(fi.LastWriteTimeUtc);
        var size = fi.Length;
        var key = CoverResizer.ETagKey(cover, mtime, size, maxW);
        var etag = "\"" + key + "\"";

        var res = ctx.Response;
        res.Headers.CacheControl = "private, max-age=604800";
        res.Headers.ETag = etag;
        res.Headers.XContentTypeOptions = "nosniff";

        var inm = ctx.Request.Headers.IfNoneMatch;
        if (inm.Count > 0 && inm.Any(v => v != null && (v.Trim() == etag || v.Split(',').Any(p => p.Trim() == etag))))
        {
            res.StatusCode = 304;
            return;
        }

        CoverResizer.Resized? resized = null;
        if (maxW > 0)
        {
            try { resized = await Task.Run(() => rt.Covers.Resize(cover, maxW, key), ctx.RequestAborted); }
            catch (OperationCanceledException) { return; }
            catch (Exception) { resized = null; }                 // 縮小に失敗したら原本を返す
        }

        if (resized != null)
        {
            res.StatusCode = 200;
            res.ContentType = resized.Mime;
            res.ContentLength = resized.Data.Length;
            if (!HttpMethods.IsHead(ctx.Request.Method)) await res.Body.WriteAsync(resized.Data, ctx.RequestAborted);
            return;
        }

        res.StatusCode = 200;
        res.ContentType = Mime.ForImageExt(LibraryIndexBuilder.ExtOf(cover));
        res.ContentLength = size;
        if (!HttpMethods.IsHead(ctx.Request.Method)) await res.SendFileAsync(cover, ctx.RequestAborted);
    }

    // ------------------------------------------------------------ json helpers

    private static Task Fail(HttpContext ctx, int status, string message) =>
        WriteJson(ctx, status, new { error = message });

    private static async Task WriteJson(HttpContext ctx, int status, object body)
    {
        var res = ctx.Response;
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.Headers.CacheControl = "no-store";
        res.Headers.XContentTypeOptions = "nosniff";
        if (HttpMethods.IsHead(ctx.Request.Method)) return;
        await JsonSerializer.SerializeAsync(res.Body, body, body.GetType(), Json, ctx.RequestAborted);
    }
}
