using LibraryServer.Core.Covers;
using LibraryServer.Server;

namespace LibraryServer.App;

/// <summary>トレイアイコンとメニュー。サーバー (ServerController) とウィンドウ (MainForm) の寿命を持つ</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly ServerOptions _opts;
    private readonly ServerController _server;
    private readonly NotifyIcon _tray;
    private readonly Icon _icon;
    private readonly ToolStripMenuItem _miOpen = new("ビューアを開く(&V)");
    private readonly ToolStripMenuItem _miBrowser = new("ブラウザで開く(&B)");
    private readonly ToolStripMenuItem _miSettings = new("設定(&S)...");
    private readonly ToolStripMenuItem _miCovers = new("表紙を生成(&C)");
    private readonly ToolStripMenuItem _miCoversStale = new("不足分 / 古いものだけ");
    private readonly ToolStripMenuItem _miCoversForce = new("すべて作り直す");
    private readonly ToolStripMenuItem _miAutoStart = new("Windows 起動時に開始(&A)") { CheckOnClick = true };
    private readonly ToolStripMenuItem _miExit = new("終了(&X)");
    private readonly ToolStripMenuItem _miStatus = new("起動中...") { Enabled = false };
    private MainForm? _form;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    public const string AppTitle = "Comic Viewer — Library Server";
    private bool _exiting;
    private bool _openedInitialFiles;
    private readonly int _uiThreadId;

    public TrayApp(ServerOptions opts)
    {
        _opts = opts;
        _icon = LoadIcon();
        // Application.Run の前でも UI スレッドに WinForms の同期コンテキストを確実に入れておく。
        // これが無いと async の継続や Post がスレッドプールで走り、ウィンドウがメッセージループの無い
        // スレッドに作られて「応答なし」になる
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }
        _uiThreadId = Environment.CurrentManagedThreadId;
        _server = new ServerController(opts, SynchronizationContext.Current!)
        {
            OwnerWindow = () => _form is { IsDisposed: false, Visible: true } ? _form : null,
        };
        _server.Started += OnServerStarted;
        _server.ExitRequested += () => _ = ExitAsync();

        var menu = new ContextMenuStrip();
        _miOpen.Font = new Font(_miOpen.Font, FontStyle.Bold);
        _miOpen.Click += (_, _) => ShowWindow("comic");
        _miBrowser.Click += (_, _) => { if (_server.IsRunning) Shell.OpenExternal(_server.BaseUrl + "comic-viewer.html"); };
        _miSettings.Click += (_, _) => ShowWindow("admin");
        _miCoversStale.Click += (_, _) => RunCovers(new CoverJob { Stale = true });
        _miCoversForce.Click += (_, _) =>
        {
            if (MessageBox.Show("既存の表紙をすべて作り直します。よろしいですか？", AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                RunCovers(new CoverJob { Force = true });
        };
        _miCovers.DropDownItems.AddRange([_miCoversStale, _miCoversForce]);
        _miAutoStart.Checked = AutoStart.IsEnabled();
        _miAutoStart.Click += (_, _) => { try { AutoStart.Set(_miAutoStart.Checked); } catch (Exception ex) { MessageBox.Show(ex.Message); } };
        _miExit.Click += (_, _) => _ = ExitAsync();
        menu.Items.AddRange([_miStatus, new ToolStripSeparator(), _miOpen, _miBrowser, _miSettings, _miCovers, new ToolStripSeparator(), _miAutoStart, new ToolStripSeparator(), _miExit]);
        menu.Opening += (_, _) => RefreshStatusItem();

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "Comic Viewer — Library Server (起動中...)",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowWindow("comic");

        AppLog.Write($"TrayApp start: args={Environment.CommandLine}");
        AutoStart.RefreshIfEnabled();
        ListenForShowRequests();
        _ = StartServerAsync();
    }

    private static Icon LoadIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(path)) return new Icon(path);
            var exe = Environment.ProcessPath;
            if (exe != null) return Icon.ExtractAssociatedIcon(exe) ?? SystemIcons.Application;
        }
        catch (Exception) { }
        return SystemIcons.Application;
    }

    private async Task StartServerAsync()
    {
        try
        {
            await _server.StartAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show("サーバーを起動できません:\n" + ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            await ExitAsync();
        }
    }

    private void OnServerStarted()
    {
        AppLog.Write($"Server started: {_server.BaseUrl} (error={_server.LastError ?? "none"}) thread={Environment.CurrentManagedThreadId} ui={_uiThreadId} loop={Application.MessageLoop}");
        _tray.Text = Trim63($"Comic Viewer — Library Server ({_server.BaseUrl})");
        RefreshStatusItem();
        if (_server.LastError != null)
        {
            _tray.ShowBalloonTip(8000, AppTitle, _server.LastError, ToolTipIcon.Warning);
        }
        var rt = _server.Runtime;
        var firstRun = rt != null && !rt.Config.IsConfigured;
        if (_form is { IsDisposed: false } && _form.Visible)
        {
            // 再起動 (ポート変更) 後: 新しい URL に移動する
            _form.Navigate(_server.AppUrl(firstRun ? "admin" : ""));
        }
        else if (!_opts.Minimized || firstRun)
        {
            ShowWindow(firstRun ? "admin" : "comic");
        }
        if (firstRun) _tray.ShowBalloonTip(6000, AppTitle, "公開するフォルダを設定画面で選んでください。", ToolTipIcon.Info);
        if (_opts.Positional.Count > 0 && !_openedInitialFiles)
        {
            _openedInitialFiles = true;
            OpenFiles(_opts.Positional);
        }
    }

    /// <summary>
    /// ファイル関連付け / コマンドラインで渡されたファイルをビューアで開く (最初の 1 つだけ)。
    /// root の中なら ?lib=<相対パス> (ライブラリ経由、しおりも共有)、外なら一時 URL (/__local/{token}) を ?open= で渡す。
    /// </summary>
    public void OpenFiles(IReadOnlyList<string> paths)
    {
        var rt = _server.Runtime;
        var reg = _server.Services?.GetService<LocalFileRegistry>();
        if (rt == null || reg == null || paths.Count == 0) return;
        string? full = null;
        foreach (var p in paths)
        {
            try { var f = Path.GetFullPath(p); if (File.Exists(f)) { full = f; break; } } catch (Exception) { }
        }
        if (full == null) return;
        var ext = Core.Index.LibraryIndexBuilder.ExtOf(full);
        if (!rt.Config.ExtSet().Contains(ext))
        {
            _tray.ShowBalloonTip(4000, AppTitle, $"この形式は開けません: {Path.GetFileName(full)}", ToolTipIcon.Warning);
            return;
        }
        string query;
        var rel = RelativeToRoot(rt.Config.Root, full);
        if (rel != null) query = "?lib=" + Uri.EscapeDataString(rel).Replace("%2F", "/");
        else query = "?open=" + Uri.EscapeDataString("/__local/" + reg.Register(full)) + "&name=" + Uri.EscapeDataString(Path.GetFileName(full));
        AppLog.Write($"OpenFile: {full} -> {(rel != null ? "lib" : "local")}");
        if (_form == null || _form.IsDisposed)
        {
            _form = new MainForm(_icon);
            _form.Navigate(_server.AppUrl("", query));
            _form.Show();
            return;
        }
        _form.OpenInViewer(query, _server.AppUrl("", query));
    }

    private static string? RelativeToRoot(string root, string full)
    {
        if (string.IsNullOrEmpty(root)) return null;
        string rootFull;
        try { rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); } catch (Exception) { return null; }
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        var rel = full[(rootFull.Length + 1)..].Replace('\\', '/');
        foreach (var seg in rel.Split('/'))
        {
            if (seg.Length == 0 || seg[0] == '.') return null;     // ドット始まりは一覧に無いので ?lib= では開けない
        }
        return rel;
    }

    /// <summary>2 つ目のインスタンスが書き残したファイル一覧 (open-request.txt) を処理する</summary>
    private void HandleShowRequest()
    {
        var req = Program.OpenRequestPath;
        try
        {
            if (File.Exists(req))
            {
                var lines = File.ReadAllLines(req).Where(l => l.Trim().Length > 0).ToList();
                File.Delete(req);
                if (lines.Count > 0) { OpenFiles(lines); return; }
            }
        }
        catch (Exception ex) { AppLog.Write("open-request failed", ex); }
        ShowWindow("comic");
    }

    private void RefreshStatusItem()
    {
        var rt = _server.Runtime;
        if (rt == null) { _miStatus.Text = "停止中"; return; }
        var idx = rt.Index.Current;
        _miStatus.Text = rt.Config.IsConfigured
            ? $"{rt.Config.DisplayLabel()} — {(idx == null ? "索引作成中" : idx.Entries.Count + " 件")} · :{rt.BoundPort}"
            : $"root 未設定 · :{rt.BoundPort}";
        _miAutoStart.Checked = AutoStart.IsEnabled();
    }

    private void ShowWindow(string tab)
    {
        AppLog.Write($"ShowWindow({tab}) running={_server.IsRunning} form={(_form == null ? "null" : _form.IsDisposed ? "disposed" : _form.Visible ? "visible" : "hidden")}");
        if (!_server.IsRunning) return;
        if (_form == null || _form.IsDisposed)
        {
            _form = new MainForm(_icon);
            _form.Navigate(_server.AppUrl(tab));
            _form.Show();
            return;
        }
        _form.ShowTab(tab, _server.AppUrl(tab));
    }

    private void RunCovers(CoverJob job)
    {
        var covers = _server.Services?.GetService<CoverAutoService>();
        if (covers == null) return;
        if (_server.Runtime?.Config.IsConfigured != true)
        {
            ShowWindow("admin");
            return;
        }
        if (!covers.StartRun(job, "manual"))
        {
            _tray.ShowBalloonTip(3000, AppTitle, "表紙の生成は既に実行中です。", ToolTipIcon.Info);
            return;
        }
        ShowWindow("admin/covers");
    }

    /// <summary>2 つ目のインスタンスが起動されたらウィンドウを前面に出す</summary>
    private void ListenForShowRequests()
    {
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            var ui = SynchronizationContext.Current;
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => ui?.Post(_ => HandleShowRequest(), null), null, -1, executeOnlyOnce: false);
        }
        catch (Exception) { }
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        AppLog.Write("Exit");
        _tray.Visible = false;
        if (_form is { IsDisposed: false }) { _form.AllowClose = true; _form.Close(); }
        try { await _server.StopAsync(); } catch (Exception) { }
        ExitThread();
    }

    private static string Trim63(string s) => s.Length <= 63 ? s : s[..60] + "...";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _showWait?.Unregister(null);
            _showEvent?.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
