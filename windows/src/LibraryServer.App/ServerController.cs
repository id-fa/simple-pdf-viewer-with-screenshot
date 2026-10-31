using LibraryServer.Core.Config;
using LibraryServer.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace LibraryServer.App;

/// <summary>
/// トレイアプリの中で Kestrel ホストを起動・停止・作り直す。
/// 設定画面 (admin.html) からの「フォルダ選択」「再起動」「自動起動」「終了」は IAppIntegration 経由でここに届く。
/// </summary>
internal sealed class ServerController : IAppIntegration, IAsyncDisposable
{
    private readonly ServerOptions _opts;
    private readonly SynchronizationContext _ui;
    private WebApplication? _app;

    public ConfigStore Store { get; }
    public string Token { get; } = AdminToken.Generate();
    public IServiceProvider? Services => _app?.Services;
    public LibraryRuntime? Runtime => _app?.Services.GetService<LibraryRuntime>();
    public RingLog? Log => _app?.Services.GetService<RingLog>();
    public bool IsRunning => _app != null;
    public string? LastError { get; private set; }

    /// <summary>(再) 起動が完了したとき。UI スレッドで呼ぶ</summary>
    public event Action? Started;
    /// <summary>設定画面から「終了」を押されたとき</summary>
    public event Action? ExitRequested;
    /// <summary>フォルダ選択ダイアログの親ウィンドウを返す</summary>
    public Func<IWin32Window?>? OwnerWindow { get; set; }

    public ServerController(ServerOptions opts, SynchronizationContext ui)
    {
        _opts = opts;
        _ui = ui;
        Store = ConfigStore.Resolve(opts.ConfigPath);
    }

    public int Port => Runtime?.BoundPort ?? 0;
    public string BaseUrl => $"http://127.0.0.1:{Port}/";
    public string AppUrl(string tab = "", string? viewerQuery = null) =>
        $"{BaseUrl}__app/app.html?token={Uri.EscapeDataString(Token)}{(viewerQuery != null ? "&v=" + Uri.EscapeDataString(viewerQuery) : "")}{(tab.Length > 0 ? "#" + tab : "")}";
    public string AdminUrl => $"{BaseUrl}__app/admin.html?token={Uri.EscapeDataString(Token)}";

    /// <summary>
    /// 起動する。設定のポートが使えないとき (他のアプリが使用中等) は、設定画面だけは開けるように
    /// localhost の空きポートで立ち上げ直す (LastError に理由を残す)。
    /// </summary>
    public async Task StartAsync()
    {
        LastError = null;
        LibraryConfig cfg;
        try
        {
            cfg = Store.LoadOrDefault();
        }
        catch (Exception ex)
        {
            LastError = $"設定ファイルを読めません: {Store.Path}\n{ex.Message}";
            cfg = new LibraryConfig().Normalize();
        }
        _opts.ApplyOverrides(cfg);

        try
        {
            await StartWithAsync(cfg);
        }
        catch (Exception ex)
        {
            LastError = $"ポート {cfg.Port} で起動できません ({ex.GetBaseException().Message})。\n設定画面を開くため、一時的にこの PC 専用の空きポートで起動します。";
            var fallback = cfg.Clone();
            fallback.Port = 0;
            fallback.Bind = "localhost";
            await StartWithAsync(fallback, fallbackPort: true);
        }
        _ui.Post(_ => Started?.Invoke(), null);
    }

    private async Task StartWithAsync(LibraryConfig cfg, bool fallbackPort = false)
    {
        var opts = new ServerOptions
        {
            Headless = false,
            Verbose = _opts.Verbose,
            ConfigPath = _opts.ConfigPath,
            AdminToken = Token,
            ConfigureServices = s => s.AddSingleton<IAppIntegration>(this),
        };
        if (fallbackPort)
        {
            // Kestrel にポート 0 を渡すには ConfigureWebHost 側で指定する
            opts.ConfigureWebHost = wh => wh.ConfigureKestrel(k => k.ListenLocalhost(0));
        }
        var app = LibraryHost.Create(Store, cfg, opts, lb => lb.ClearProviders().AddProvider(new RingLoggerProvider(new RingLog())));
        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
        _app = app;
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        if (app == null) return;
        try { await app.StopAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        try { await app.DisposeAsync(); } catch (Exception) { }
    }

    // ------------------------------------------------------------ IAppIntegration

    public bool IsApp => true;
    public bool CanRestart => true;
    public bool? AutoStartEnabled => AutoStart.IsEnabled();
    public void SetAutoStart(bool enabled) => AutoStart.Set(enabled);

    public Task<string?> PickFolderAsync(string? initial)
    {
        var tcs = new TaskCompletionSource<string?>();
        _ui.Post(_ =>
        {
            try
            {
                using var dlg = new FolderBrowserDialog
                {
                    Description = "ライブラリとして公開するフォルダを選んでください",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = false,
                };
                if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) dlg.InitialDirectory = initial;
                var owner = OwnerWindow?.Invoke();
                var r = owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
                tcs.SetResult(r == DialogResult.OK ? dlg.SelectedPath : null);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }, null);
        return tcs.Task;
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    public void Exit() => _ui.Post(_ => ExitRequested?.Invoke(), null);

    public async ValueTask DisposeAsync() => await StopAsync();
}
