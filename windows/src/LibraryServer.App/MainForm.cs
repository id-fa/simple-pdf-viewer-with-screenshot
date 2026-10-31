using System.Diagnostics;
using LibraryServer.Core.Config;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LibraryServer.App;

/// <summary>
/// WebView2 1 枚のウィンドウ。中身は同じサーバーが配信する app.html (コミック / PDF / 設定 のタブ)。
/// 閉じるボタンは「隠す」だけで、終了はトレイメニューから。
/// </summary>
internal sealed class MainForm : Form
{
    private readonly WebView2 _web = new();
    private readonly Label _status = new();
    private bool _ready;
    private string? _pendingUrl;
    public bool AllowClose { get; set; }

    public MainForm(Icon icon)
    {
        Text = "Comic Viewer — Library Server";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 860);
        MinimumSize = new Size(640, 480);
        BackColor = Color.FromArgb(0x1e, 0x29, 0x3b);

        _status.Dock = DockStyle.Fill;
        _status.ForeColor = Color.FromArgb(0x94, 0xa3, 0xb8);
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _status.Font = new Font(Font.FontFamily, 11f);
        _status.Text = "起動中...";
        Controls.Add(_status);

        _web.Dock = DockStyle.Fill;
        _web.Visible = false;
        _web.DefaultBackgroundColor = Color.FromArgb(0x1e, 0x29, 0x3b);
        Controls.Add(_web);
        _web.BringToFront();

        Shown += async (_, _) => await InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        if (_ready) return;
        AppLog.Write("WebView2: init");
        try
        {
            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ConfigStore.AppFolderName, "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
            // target=_blank や window.open は既定のブラウザへ
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                Shell.OpenExternal(e.Uri);
            };
            core.DocumentTitleChanged += (_, _) =>
            {
                var t = core.DocumentTitle;
                Text = string.IsNullOrWhiteSpace(t) ? "Comic Viewer — Library Server" : t;
            };
            _ready = true;
            AppLog.Write("WebView2: ready " + core.Environment.BrowserVersionString);
            _web.Visible = true;
            _status.Visible = false;
            if (_pendingUrl != null) { var u = _pendingUrl; _pendingUrl = null; Navigate(u); }
        }
        catch (Exception ex)
        {
            AppLog.Write("WebView2: init failed", ex);
            _status.Text = "WebView2 ランタイムを初期化できません。\nMicrosoft Edge WebView2 Runtime をインストールしてください。\n\n" + ex.Message
                         + "\n\nブラウザでは引き続き使えます (トレイメニュー →「ブラウザで開く」)。";
        }
    }

    public void Navigate(string url)
    {
        AppLog.Write("MainForm: navigate " + url.Split('?')[0]);
        if (!_ready) { _pendingUrl = url; return; }
        try { _web.CoreWebView2.Navigate(url); } catch (Exception) { _pendingUrl = url; }
    }

    /// <summary>app.html のタブを切り替える (読み込み済みなら JS で、そうでなければ URL から)</summary>
    public async void ShowTab(string tab, string appUrl)
    {
        ShowAndActivate();
        if (_ready && _web.Source != null && _web.Source.AbsolutePath.EndsWith("/__app/app.html", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await _web.CoreWebView2.ExecuteScriptAsync($"window.__showTab && window.__showTab({System.Text.Json.JsonSerializer.Serialize(tab)})");
                return;
            }
            catch (Exception) { }
        }
        Navigate(appUrl);
    }

    /// <summary>コミックビューアを指定クエリ (?lib=... / ?open=...) で開く</summary>
    public async void OpenInViewer(string viewerQuery, string appUrlWithQuery)
    {
        ShowAndActivate();
        if (_ready && _web.Source != null && _web.Source.AbsolutePath.EndsWith("/__app/app.html", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await _web.CoreWebView2.ExecuteScriptAsync($"window.__openInViewer && window.__openInViewer({System.Text.Json.JsonSerializer.Serialize(viewerQuery)})");
                return;
            }
            catch (Exception) { }
        }
        Navigate(appUrlWithQuery);
    }

    public void ShowAndActivate()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        AppLog.Write($"MainForm: closing ({e.CloseReason}, allowClose={AllowClose})");
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}

/// <summary>既定のブラウザで URL を開く</summary>
internal static class Shell
{
    public static void OpenExternal(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { }
    }
}
