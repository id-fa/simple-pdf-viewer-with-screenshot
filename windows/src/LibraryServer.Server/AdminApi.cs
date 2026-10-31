using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using LibraryServer.Core.Auth;
using LibraryServer.Core.Config;
using LibraryServer.Core.Covers;
using QRCoder;

namespace LibraryServer.Server;

/// <summary>
/// 設定画面 (admin.html) とトレイアプリが使う管理 API。/__admin/* 配下。
/// すべて「ループバックからの接続」かつ「起動時トークン (X-Admin-Token ヘッダー or ?token=)」を要求する。
/// LAN のクライアント (スマホ等) からは 403。
/// </summary>
public static class AdminApi
{
    public const string Prefix = "/__admin";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup(Prefix);
        g.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var token = http.RequestServices.GetRequiredService<AdminToken>();
            if (!IsLoopback(http) || !token.Matches(http.Request.Headers["X-Admin-Token"].FirstOrDefault() ?? http.Request.Query["token"].FirstOrDefault()))
            {
                return Results.Json(new { error = "管理 API はこの PC 上からのみ使えます / The admin API is only available from this machine" }, Json, statusCode: 403);
            }
            return await next(ctx);
        });

        g.MapGet("/status", Status);
        g.MapGet("/config", GetConfig);
        g.MapPost("/config", SaveConfig);
        g.MapPost("/covers/run", CoversRun);
        g.MapPost("/covers/cancel", (CoverAutoService covers) => { covers.Cancel(); return Results.Json(new { ok = true }, Json); });
        g.MapGet("/covers/events", CoverEvents);
        g.MapGet("/log", (RingLog log) => Results.Json(new { lines = log.Snapshot() }, Json));
        g.MapPost("/index/rebuild", async (LibraryRuntime rt) =>
        {
            try { var s = await rt.Index.RebuildAsync(); return Results.Json(new { ok = true, count = s.Entries.Count, ms = (int)s.BuildDuration.TotalMilliseconds }, Json); }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, Json, statusCode: 500); }
        });
        g.MapPost("/pick-folder", async (IAppIntegration appi, LibraryRuntime rt) =>
        {
            if (!appi.IsApp) return Results.Json(new { error = "フォルダ選択はトレイアプリでのみ使えます / only available in the tray app" }, Json, statusCode: 501);
            var picked = await appi.PickFolderAsync(rt.Config.Root);
            return Results.Json(new { path = picked }, Json);
        });
        g.MapPost("/restart", (IAppIntegration appi) =>
        {
            if (!appi.CanRestart) return Results.Json(new { error = "再起動はトレイアプリでのみ使えます / only available in the tray app" }, Json, statusCode: 501);
            _ = Task.Run(async () => { await Task.Delay(300); await appi.RestartAsync(); });
            return Results.Json(new { ok = true }, Json);
        });
        g.MapPost("/autostart", async (HttpContext ctx, IAppIntegration appi) =>
        {
            if (appi.AutoStartEnabled == null) return Results.Json(new { error = "この環境では設定できません / not available here" }, Json, statusCode: 501);
            var body = await JsonSerializer.DeserializeAsync<AutoStartBody>(ctx.Request.Body, Json);
            appi.SetAutoStart(body?.Enabled ?? false);
            return Results.Json(new { ok = true, enabled = appi.AutoStartEnabled }, Json);
        });
        g.MapPost("/exit", (IAppIntegration appi) =>
        {
            if (!appi.IsApp) return Results.Json(new { error = "not available" }, Json, statusCode: 501);
            _ = Task.Run(async () => { await Task.Delay(200); appi.Exit(); });
            return Results.Json(new { ok = true }, Json);
        });
        g.MapGet("/qr.png", (string url) =>
        {
            if (url.Length > 512 || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(8);
            return Results.File(png, "image/png");
        });
    }

    private sealed record AutoStartBody(bool Enabled);

    private static bool IsLoopback(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress;
        if (ip == null) return true;                        // TestServer / 名前付きパイプ
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return IPAddress.IsLoopback(ip);
    }

    // ------------------------------------------------------------ status / config

    private static IResult Status(LibraryRuntime rt, CoverAutoService covers, IAppIntegration appi, AdminToken token)
    {
        var cfg = rt.Config;
        var snap = rt.Index.Current;
        return Results.Json(new
        {
            version = typeof(AdminApi).Assembly.GetName().Version?.ToString(3),
            startedAt = rt.StartedAt,
            configured = cfg.IsConfigured,
            root = cfg.Root,
            rootExists = cfg.IsConfigured && Directory.Exists(cfg.Root),
            label = cfg.DisplayLabel(),
            port = rt.BoundPort,
            bind = rt.BoundLocalhostOnly ? "localhost" : "lan",
            urls = rt.Urls(),
            configPath = rt.Store.Path,
            configExists = rt.Store.Exists,
            dataDir = rt.Store.DataDirectory,
            restartRequired = rt.RequiresRestart(cfg),
            index = snap == null ? null : new
            {
                count = snap.Entries.Count,
                covers = snap.Entries.Count(e => e.HasCover),
                generated = snap.Generated,
                buildMs = (int)snap.BuildDuration.TotalMilliseconds,
                truncated = snap.Truncated,
                warnings = snap.Warnings,
            },
            covers = covers.Snapshot(),
            app = new { isApp = appi.IsApp, canRestart = appi.CanRestart, autoStart = appi.AutoStartEnabled },
        }, Json);
    }

    private static IResult GetConfig(LibraryRuntime rt)
    {
        var cfg = rt.Config.Clone();
        var hasPass = cfg.Auth != null && cfg.Auth.Pass.Length > 0;
        if (cfg.Auth != null) cfg.Auth.Pass = "";
        return Results.Json(new { config = cfg, hasPass, configPath = rt.Store.Path }, LibraryConfig.JsonOptions);
    }

    private sealed class SaveConfigBody
    {
        public LibraryConfig? Config { get; set; }
    }

    private static async Task<IResult> SaveConfig(HttpContext ctx, LibraryRuntime rt)
    {
        LibraryConfig? incoming;
        try
        {
            var body = await JsonSerializer.DeserializeAsync<SaveConfigBody>(ctx.Request.Body, LibraryConfig.JsonOptions, ctx.RequestAborted);
            incoming = body?.Config;
        }
        catch (JsonException ex)
        {
            return Results.Json(new { error = "設定の形式が不正です / invalid JSON: " + ex.Message }, Json, statusCode: 400);
        }
        if (incoming == null) return Results.Json(new { error = "config がありません / missing config" }, Json, statusCode: 400);

        var old = rt.Config;
        // パスワード: 空なら既存を維持、平文なら bcrypt 化、ハッシュ ($2...) ならそのまま
        if (incoming.Auth != null)
        {
            if (string.IsNullOrEmpty(incoming.Auth.User)) incoming.Auth = null;
            else if (string.IsNullOrEmpty(incoming.Auth.Pass))
            {
                if (old.Auth != null && old.Auth.User == incoming.Auth.User) incoming.Auth.Pass = old.Auth.Pass;
                else if (old.Auth != null) incoming.Auth.Pass = old.Auth.Pass;
                else return Results.Json(new { error = "パスワードを入力してください / a password is required" }, Json, statusCode: 400);
            }
            else if (!incoming.Auth.Pass.StartsWith("$2", StringComparison.Ordinal))
            {
                incoming.Auth.Pass = BasicAuth.HashPassword(incoming.Auth.Pass);
            }
        }
        incoming.Normalize();
        if (incoming.IsConfigured && !Directory.Exists(incoming.Root))
        {
            return Results.Json(new { error = "root フォルダが見つかりません / The root folder was not found: " + incoming.Root }, Json, statusCode: 400);
        }
        if (!incoming.CoverExts.Contains(incoming.Covers.OutputExt))
        {
            return Results.Json(new { error = $"表紙の出力形式 .{incoming.Covers.OutputExt} が coverExts に含まれていません / cover format not in coverExts" }, Json, statusCode: 400);
        }

        try
        {
            rt.Apply(incoming, save: true);
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = "設定を保存できません / cannot save: " + ex.Message }, Json, statusCode: 500);
        }
        return Results.Json(new { ok = true, restartRequired = rt.RequiresRestart(incoming), urls = rt.Urls(), configPath = rt.Store.Path }, Json);
    }

    // ------------------------------------------------------------ covers

    private sealed class CoversRunBody
    {
        public string Mode { get; set; } = "stale";     // stale | force | missing
        public bool DryRun { get; set; }
        public string? Filter { get; set; }
    }

    private static async Task<IResult> CoversRun(HttpContext ctx, CoverAutoService covers, LibraryRuntime rt)
    {
        CoversRunBody? body = null;
        if (ctx.Request.ContentLength > 0)
        {
            try { body = await JsonSerializer.DeserializeAsync<CoversRunBody>(ctx.Request.Body, Json, ctx.RequestAborted); }
            catch (JsonException) { }
        }
        body ??= new CoversRunBody();
        if (!rt.Config.IsConfigured) return Results.Json(new { error = "root が未設定です / root is not set" }, Json, statusCode: 400);
        var job = new CoverJob
        {
            Force = body.Mode == "force",
            Stale = body.Mode == "stale",
            DryRun = body.DryRun,
            Filter = string.IsNullOrWhiteSpace(body.Filter) ? null : body.Filter,
        };
        var started = covers.StartRun(job, "manual");
        return Results.Json(new { ok = started, running = !started }, Json, statusCode: started ? 200 : 409);
    }

    /// <summary>Server-Sent Events: 表紙生成の 1 件ごとの進捗と状態変化</summary>
    private static async Task CoverEvents(HttpContext ctx, CoverAutoService covers, RingLog log)
    {
        var res = ctx.Response;
        res.StatusCode = 200;
        res.ContentType = "text/event-stream; charset=utf-8";
        res.Headers.CacheControl = "no-cache";
        res.Headers["X-Accel-Buffering"] = "no";

        var ch = Channel.CreateUnbounded<(string Event, object Data)>();
        void OnProgress(CoverEvent ev) => ch.Writer.TryWrite(("cover", ev));
        void OnState() => ch.Writer.TryWrite(("state", covers.Snapshot()));
        void OnLog(LogLine l) => ch.Writer.TryWrite(("log", l));
        covers.Progress += OnProgress;
        covers.StateChanged += OnState;
        log.Added += OnLog;
        try
        {
            await Write("state", covers.Snapshot());
            var ct = ctx.RequestAborted;
            while (!ct.IsCancellationRequested)
            {
                var readTask = ch.Reader.ReadAsync(ct).AsTask();
                var done = await Task.WhenAny(readTask, Task.Delay(15000, ct));
                if (done == readTask)
                {
                    var (ev, data) = await readTask;
                    await Write(ev, data);
                }
                else
                {
                    await res.WriteAsync(": ping\n\n", ct);
                    await res.Body.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            covers.Progress -= OnProgress;
            covers.StateChanged -= OnState;
            log.Added -= OnLog;
        }

        async Task Write(string ev, object data)
        {
            await res.WriteAsync($"event: {ev}\ndata: {JsonSerializer.Serialize(data, data.GetType(), Json)}\n\n", ctx.RequestAborted);
            await res.Body.FlushAsync(ctx.RequestAborted);
        }
    }
}
