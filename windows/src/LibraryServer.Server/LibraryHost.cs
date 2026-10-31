using LibraryServer.Core.Config;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace LibraryServer.Server;

/// <summary>Kestrel ホストの組み立て。ヘッドレス (Program.cs) とトレイアプリの両方から使う</summary>
public static class LibraryHost
{
    public const string ServiceName = "LibraryServer";

    /// <summary>ループバックからしか配信しない管理画面。/__ 始まりなので sw.js のキャッシュ対象外 (exe 更新が即反映される)</summary>
    private static readonly string[] LocalOnlyPages = ["/__app/admin.html", "/__app/app.html"];

    public static WebApplication Create(ConfigStore store, LibraryConfig cfg, ServerOptions opts, Action<ILoggingBuilder>? logging = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            // 既定ではエントリアセンブリ名になり、Hosting がその名前のアセンブリから HostingStartup を探しに行く。
            // トレイアプリ (別 exe) から使っても同じ挙動になるよう、このアセンブリ名に固定する
            ApplicationName = typeof(LibraryHost).Assembly.GetName().Name,
        });

        var ring = new RingLog();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.AddProvider(new RingLoggerProvider(ring));
        builder.Logging.SetMinimumLevel(opts.Verbose ? LogLevel.Debug : LogLevel.Information);
        builder.Logging.AddFilter("Microsoft", opts.Verbose ? LogLevel.Information : LogLevel.Warning);
        logging?.Invoke(builder.Logging);

        if (opts.Service)
        {
            builder.Host.UseWindowsService(o => o.ServiceName = ServiceName);
        }

        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(ring);
        builder.Services.AddSingleton(new AdminToken(opts.AdminToken));
        builder.Services.AddSingleton<LocalFileRegistry>();
        builder.Services.AddSingleton(sp => new LibraryRuntime(store, cfg, sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddSingleton<CoverAutoService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CoverAutoService>());
        opts.ConfigureServices?.Invoke(builder.Services);
        builder.Services.TryAddSingleton<IAppIntegration, NullAppIntegration>();

        if (opts.ConfigureWebHost != null)
        {
            opts.ConfigureWebHost(builder.WebHost);
        }
        else
        {
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Limits.MaxRequestBodySize = 1024 * 1024;
                k.AddServerHeader = false;
                if (cfg.BindLocalhostOnly) k.ListenLocalhost(cfg.Port);
                else k.ListenAnyIP(cfg.Port);
            });
        }

        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            // wasm-vips の crossOriginIsolated 用 (sw.js が付けているものと同じ)。設定で ON のときだけ
            var rt = ctx.RequestServices.GetRequiredService<LibraryRuntime>();
            if (rt.Config.Coi)
            {
                var h = ctx.Response.Headers;
                h["Cross-Origin-Embedder-Policy"] = "require-corp";
                h["Cross-Origin-Opener-Policy"] = "same-origin";
                h["Cross-Origin-Resource-Policy"] = "cross-origin";
            }
            // 管理画面の HTML (/__app/) は LAN に見せない (API 自体は別途トークンで守られている)
            if (LocalOnlyPages.Any(p => ctx.Request.Path.Equals(p, StringComparison.OrdinalIgnoreCase)))
            {
                var ip = ctx.Connection.RemoteIpAddress;
                if (ip != null && !System.Net.IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip))
                {
                    ctx.Response.StatusCode = 404;
                    return;
                }
            }
            await next();
        });

        LibraryApi.Map(app);
        AdminApi.Map(app);
        MapLocalFiles(app);
        MapStatic(app);

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var rt = app.Services.GetRequiredService<LibraryRuntime>();
            var token = app.Services.GetRequiredService<AdminToken>();
            var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LibraryServer");
            // ポート 0 (空きポート) で起動したときは実際に割り当てられた番号を控える
            var bound = app.Urls.Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri.Port : 0).FirstOrDefault(p => p > 0);
            if (bound > 0) rt.BoundPort = bound;
            foreach (var u in rt.Urls()) log.LogInformation("Listening: {Url}comic-viewer.html", u);
            log.LogInformation("設定画面 / Admin: http://127.0.0.1:{Port}/__app/admin.html?token={Token}", rt.BoundPort, token.Value);
            if (!rt.Config.IsConfigured) log.LogWarning("root が未設定です。設定画面で公開フォルダを選ぶか、library.config.json の root か --root=DIR を指定してください ({Path})", store.Path);
            else _ = rt.Index.RebuildAsync().ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
        });
        app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<LibraryRuntime>().Dispose());

        return app;
    }

    /// <summary>
    /// GET /__local/{token} — トレイアプリが登録した root 外のローカルファイルを 1 本だけ配信する
    /// (comic-viewer.html の ?open= が取りに来る)。ループバック以外は 404
    /// </summary>
    private static void MapLocalFiles(WebApplication app)
    {
        app.MapGet("/__local/{token}", async (HttpContext ctx, string token, LocalFileRegistry reg) =>
        {
            var ip = ctx.Connection.RemoteIpAddress;
            if (ip != null && !System.Net.IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            var path = reg.Resolve(token);
            if (path == null || !File.Exists(path))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            var name = Path.GetFileName(path);
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            ctx.Response.Headers.ContentDisposition = "inline; filename*=UTF-8''" + Uri.EscapeDataString(name);
            await Results.File(path, Core.Mime.ForBookExt(Core.Index.LibraryIndexBuilder.ExtOf(name)), enableRangeProcessing: true).ExecuteAsync(ctx);
        });
    }

    /// <summary>ビューア一式 (埋め込み wwwroot、または exe の隣の wwwroot/ フォルダ) を配信する</summary>
    private static void MapStatic(WebApplication app)
    {
        IFileProvider provider = new ManifestEmbeddedFileProvider(typeof(LibraryHost).Assembly, "wwwroot");
        var physical = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(physical))
        {
            provider = new CompositeFileProvider(new PhysicalFileProvider(physical), provider);
        }

        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".mjs"] = "text/javascript";
        types.Mappings[".js"] = "text/javascript";
        types.Mappings[".wasm"] = "application/wasm";
        types.Mappings[".webmanifest"] = "application/manifest+json";
        types.Mappings[".bcmap"] = "application/octet-stream";
        types.Mappings[".pfb"] = "application/octet-stream";

        app.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = provider,
            DefaultFileNames = ["comic-viewer.html"],
        });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = provider,
            ContentTypeProvider = types,
            ServeUnknownFileTypes = true,
            DefaultContentType = "application/octet-stream",
            OnPrepareResponse = ctx =>
            {
                // HTML / sw.js / manifest は常に再検証させる (Service Worker の更新検知を妨げない)。
                // vendor 以下はバージョン固定なので短期キャッシュを許す
                var name = ctx.File.Name;
                var revalidate = name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                                 || name.Equals("sw.js", StringComparison.OrdinalIgnoreCase)
                                 || name.EndsWith(".webmanifest", StringComparison.OrdinalIgnoreCase);
                ctx.Context.Response.Headers.CacheControl = revalidate ? "no-cache" : "public, max-age=86400";
            },
        });
    }
}
