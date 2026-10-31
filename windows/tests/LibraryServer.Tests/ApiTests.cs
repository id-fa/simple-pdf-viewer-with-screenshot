using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LibraryServer.Core.Config;
using LibraryServer.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LibraryServer.Tests;

/// <summary>TestServer 上で library.php 互換 API を叩く</summary>
public sealed class ApiFixture : IAsyncDisposable
{
    public TempLibrary Lib { get; } = new();
    public WebApplication App { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public LibraryRuntime Runtime => App.Services.GetRequiredService<LibraryRuntime>();

    public static async Task<ApiFixture> StartAsync(Action<LibraryConfig>? mutate = null, bool seed = true)
    {
        var f = new ApiFixture();
        if (seed)
        {
            f.Lib.Write("Manga/vol01.cbz", TempLibrary.Bytes(1000));
            f.Lib.Write("Manga/vol01.cbz.coverimage.png", TempLibrary.TinyPng);
            f.Lib.Write("Manga/vol02.cbz", TempLibrary.Bytes(500));
            f.Lib.Write("Docs/guide.pdf", TempLibrary.Bytes(2048));
            f.Lib.Write(".hidden/secret.cbz", TempLibrary.Bytes(10));
            f.Lib.Write("Manga/readme.txt", "no");
            f.Lib.Write("日本語/漫画 テスト.cbz", TempLibrary.Bytes(77));
        }
        var cfg = f.Lib.Config(c =>
        {
            c.Covers.Auto = false;      // 自動生成がテスト用フォルダに表紙を書かないように
            mutate?.Invoke(c);
        });
        var store = new ConfigStore(Path.Combine(f.Lib.Root, ".cfg", "library.config.json"));
        var opts = new ServerOptions { Headless = true, ConfigureWebHost = b => b.UseTestServer() };
        f.App = LibraryHost.Create(store, cfg, opts, lb => lb.ClearProviders());
        await f.App.StartAsync();
        f.Client = f.App.GetTestClient();
        return f;
    }

    public Task<HttpResponseMessage> Get(string query, Action<HttpRequestMessage>? mutate = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/library.php" + query);
        mutate?.Invoke(req);
        return Client.SendAsync(req);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        Lib.Dispose();
    }
}

public class ApiTests
{
    private static async Task<JsonDocument> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync());

    [Fact]
    public async Task Unconfigured_root_returns_503_json()
    {
        await using var f = await ApiFixture.StartAsync(c => c.Root = "", seed: false);
        var r = await f.Get("?action=ping");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.StartsWith("application/json", r.Content.Headers.ContentType!.ToString());
        using var j = await Json(r);
        Assert.True(j.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Ping_returns_label_and_exts()
    {
        await using var f = await ApiFixture.StartAsync(c => c.Label = "My Lib");
        var r = await f.Get("?action=ping");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl!.ToString());
        using var j = await Json(r);
        Assert.True(j.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("My Lib", j.RootElement.GetProperty("root").GetString());
        Assert.Equal("pdf", j.RootElement.GetProperty("exts")[0].GetString());
    }

    [Fact]
    public async Task Unknown_action_is_400()
    {
        await using var f = await ApiFixture.StartAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Get("?action=nope")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Get("")).StatusCode);
    }

    [Fact]
    public async Task Tree_has_php_compatible_shape()
    {
        await using var f = await ApiFixture.StartAsync();
        var r = await f.Get("?action=tree");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var text = await r.Content.ReadAsStringAsync();
        Assert.Contains("漫画 テスト", text);                       // JSON_UNESCAPED_UNICODE 相当
        Assert.DoesNotContain("\\/", text);                          // JSON_UNESCAPED_SLASHES 相当
        using var j = JsonDocument.Parse(text);
        var root = j.RootElement;
        Assert.Equal(Path.GetFileName(f.Lib.Root), root.GetProperty("root").GetString());
        Assert.True(root.GetProperty("generated").GetInt64() > 1_700_000_000);
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("warnings").ValueKind);

        var entries = root.GetProperty("entries").EnumerateArray().ToDictionary(e => e.GetProperty("path").GetString()!);
        Assert.Equal(["Docs/guide.pdf", "Manga/vol01.cbz", "Manga/vol02.cbz", "日本語/漫画 テスト.cbz"], entries.Keys.OrderBy(k => k, StringComparer.Ordinal));
        var v1 = entries["Manga/vol01.cbz"];
        Assert.Equal(1000, v1.GetProperty("size").GetInt64());
        Assert.True(v1.GetProperty("mtime").GetInt64() > 1_600_000_000);
        Assert.True(v1.GetProperty("cover").GetBoolean());
        Assert.False(entries["Manga/vol02.cbz"].TryGetProperty("cover", out _));   // 無いときはキーごと省く
        Assert.False(v1.TryGetProperty("name", out _));                            // name は無い
    }

    [Fact]
    public async Task File_streams_with_headers_and_ranges()
    {
        await using var f = await ApiFixture.StartAsync();
        var r = await f.Get("?action=file&path=Manga/vol01.cbz");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/vnd.comicbook+zip", r.Content.Headers.ContentType!.MediaType);
        Assert.Equal(1000, r.Content.Headers.ContentLength);
        Assert.Equal("bytes", r.Headers.AcceptRanges.Single());
        Assert.Equal("no-store", r.Headers.CacheControl!.ToString());
        Assert.Equal("inline; filename*=UTF-8''vol01.cbz", r.Content.Headers.GetValues("Content-Disposition").Single());
        Assert.Equal(TempLibrary.Bytes(1000), await r.Content.ReadAsByteArrayAsync());

        // 途中から再開 (libDownload が投げる形)
        var r2 = await f.Get("?action=file&path=Manga/vol01.cbz", m => m.Headers.Range = new RangeHeaderValue(600, null));
        Assert.Equal(HttpStatusCode.PartialContent, r2.StatusCode);
        Assert.Equal("bytes 600-999/1000", r2.Content.Headers.ContentRange!.ToString());
        Assert.Equal(400, (await r2.Content.ReadAsByteArrayAsync()).Length);

        // 末尾 N バイト
        var r3 = await f.Get("?action=file&path=Manga/vol01.cbz", m => m.Headers.Range = new RangeHeaderValue(null, 100));
        Assert.Equal(HttpStatusCode.PartialContent, r3.StatusCode);
        Assert.Equal("bytes 900-999/1000", r3.Content.Headers.ContentRange!.ToString());

        // 範囲外
        var r4 = await f.Get("?action=file&path=Manga/vol01.cbz", m => m.Headers.Range = new RangeHeaderValue(5000, null));
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, r4.StatusCode);
        Assert.Equal("bytes */1000", r4.Content.Headers.ContentRange!.ToString());
    }

    [Fact]
    public async Task File_unicode_and_case_insensitive_lookup()
    {
        await using var f = await ApiFixture.StartAsync();
        var r = await f.Get("?action=file&path=" + Uri.EscapeDataString("日本語/漫画 テスト.cbz"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("inline; filename*=UTF-8''" + Uri.EscapeDataString("漫画 テスト.cbz"), r.Content.Headers.GetValues("Content-Disposition").Single());
        var r2 = await f.Get("?action=file&path=manga/VOL01.CBZ");
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../library.config.json")]
    [InlineData("..%2F..%2Fwindows%2Fwin.ini")]
    [InlineData(".hidden/secret.cbz")]
    [InlineData("Manga/readme.txt")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("CON")]
    [InlineData("Manga/vol01.cbz:stream")]
    [InlineData("Manga/VOL01~1.CBZ")]
    [InlineData("Manga/vol01.cbz.coverimage.png")]
    [InlineData("%00Manga/vol01.cbz")]
    public async Task File_rejects_paths_outside_the_index(string path)
    {
        await using var f = await ApiFixture.StartAsync();
        var r = await f.Get("?action=file&path=" + path);
        Assert.True(r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"{path} → {(int)r.StatusCode}");
        Assert.StartsWith("application/json", r.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task Cover_serves_original_with_etag_and_304()
    {
        await using var f = await ApiFixture.StartAsync();
        var r = await f.Get("?action=cover&path=Manga/vol01.cbz&w=320");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("image/png", r.Content.Headers.ContentType!.MediaType);      // 1x1 なので縮小されず原本
        Assert.True(r.Headers.CacheControl!.Private);
        Assert.Equal(TimeSpan.FromDays(7), r.Headers.CacheControl.MaxAge);
        var etag = r.Headers.ETag!;
        Assert.False(etag.IsWeak);
        Assert.Equal(TempLibrary.TinyPng, await r.Content.ReadAsByteArrayAsync());

        var r2 = await f.Get("?action=cover&path=Manga/vol01.cbz&w=320", m => m.Headers.IfNoneMatch.Add(etag));
        Assert.Equal(HttpStatusCode.NotModified, r2.StatusCode);

        // w が違えば別の ETag
        var r3 = await f.Get("?action=cover&path=Manga/vol01.cbz&w=160");
        Assert.NotEqual(etag.Tag, r3.Headers.ETag!.Tag);

        // 表紙が無い本は 404 JSON
        var r4 = await f.Get("?action=cover&path=Manga/vol02.cbz");
        Assert.Equal(HttpStatusCode.NotFound, r4.StatusCode);
        Assert.StartsWith("application/json", r4.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task Cover_added_after_indexing_is_found_on_request()
    {
        await using var f = await ApiFixture.StartAsync();
        await f.Get("?action=tree");
        f.Lib.Write("Manga/vol02.cbz.coverimage.png", TempLibrary.TinyPng);
        var r = await f.Get("?action=cover&path=Manga/vol02.cbz");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Basic_auth_gates_everything()
    {
        await using var f = await ApiFixture.StartAsync(c => c.Auth = new AuthConfig { User = "me", Pass = "pw", Realm = "Lib" });
        var r = await f.Get("?action=ping");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("Basic realm=\"Lib\"", r.Headers.WwwAuthenticate.ToString());
        Assert.StartsWith("application/json", r.Content.Headers.ContentType!.ToString());

        var ok = await f.Get("?action=ping", m => m.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("me:pw"))));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Static_viewer_is_served_from_embedded_resources()
    {
        await using var f = await ApiFixture.StartAsync(seed: false);
        var html = await f.Client.GetAsync("/comic-viewer.html");
        Assert.Equal(HttpStatusCode.OK, html.StatusCode);
        Assert.Equal("text/html", html.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache", html.Headers.CacheControl!.ToString());
        Assert.Contains("LIBRARY_API", await html.Content.ReadAsStringAsync());

        var root = await f.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Equal("text/html", root.Content.Headers.ContentType!.MediaType);

        var sw = await f.Client.GetAsync("/sw.js");
        Assert.Equal("text/javascript", sw.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache", sw.Headers.CacheControl!.ToString());

        var mjs = await f.Client.GetAsync("/vendor/pdfjs/pdf.min.mjs");
        Assert.Equal("text/javascript", mjs.Content.Headers.ContentType!.MediaType);
        Assert.Equal("public, max-age=86400", mjs.Headers.CacheControl!.ToString());

        var wasm = await f.Client.GetAsync("/vendor/vips/vips.wasm");
        Assert.Equal("application/wasm", wasm.Content.Headers.ContentType!.MediaType);

        var bcmap = await f.Client.GetAsync("/vendor/pdfjs/cmaps/90ms-RKSJ-H.bcmap");
        Assert.Equal(HttpStatusCode.OK, bcmap.StatusCode);

        var manifest = await f.Client.GetAsync("/manifest.webmanifest");
        Assert.Equal("application/manifest+json", manifest.Content.Headers.ContentType!.MediaType);

        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync("/library.config.json")).StatusCode);
    }

    [Fact]
    public async Task Coi_headers_only_when_enabled()
    {
        await using var f = await ApiFixture.StartAsync(c => c.Coi = true, seed: false);
        var r = await f.Client.GetAsync("/comic-viewer.html");
        Assert.Equal("require-corp", r.Headers.GetValues("Cross-Origin-Embedder-Policy").Single());
        var cfg = f.Runtime.Config.Clone();
        cfg.Coi = false;
        f.Runtime.Apply(cfg, save: false);
        var r2 = await f.Client.GetAsync("/comic-viewer.html");
        Assert.False(r2.Headers.Contains("Cross-Origin-Embedder-Policy"));
    }
}
