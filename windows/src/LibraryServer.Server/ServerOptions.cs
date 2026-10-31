using LibraryServer.Core.Config;

namespace LibraryServer.Server;

/// <summary>コマンドライン。トレイアプリ (LibraryServer.App) からも同じ解析を使う</summary>
public sealed class ServerOptions
{
    /// <summary>"" (サーバー) / "covers" (表紙生成 CLI)</summary>
    public string Command { get; set; } = "";
    public bool Help { get; set; }
    public bool Headless { get; set; }
    public bool Service { get; set; }
    /// <summary>トレイアプリ: ウィンドウを開かずトレイだけで起動 (ログオン時の自動起動用)</summary>
    public bool Minimized { get; set; }
    public string? ConfigPath { get; set; }
    public int? Port { get; set; }
    public string? Bind { get; set; }
    public string? Root { get; set; }
    /// <summary>テストから Kestrel の代わりに TestServer を差し込むためのフック (設定されると Kestrel を構成しない)</summary>
    public Action<IWebHostBuilder>? ConfigureWebHost { get; set; }
    /// <summary>トレイアプリが IAppIntegration 等を差し込むためのフック</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }
    /// <summary>管理 API のトークンを固定したいとき (トレイアプリが再起動をまたいで同じ値を使う)</summary>
    public string? AdminToken { get; set; }
    public bool Verbose { get; set; }
    /// <summary>解析しなかった残り (covers サブコマンド用)</summary>
    public List<string> Rest { get; } = [];
    public List<string> Errors { get; } = [];
    /// <summary>オプションでない引数 (トレイアプリに渡されたファイルパス等)</summary>
    public List<string> Positional { get; } = [];

    public const string Usage = """
        LibraryServer — Comic / PDF Viewer のライブラリサーバー (library.php 互換)

        使い方:
          LibraryServer.Host [--headless] [--config=PATH] [--port=N] [--bind=lan|localhost] [--root=DIR]
          LibraryServer.Host --service               Windows サービスとして動作 (sc create で登録したとき)
          LibraryServer.Host covers [options]        表紙画像を生成 (covers --help で詳細)

        オプション:
          --config=PATH   library.config.json の場所 (既定: exe の隣 → %LocalAppData%\Comic-Viewer-F → %ProgramData%\Comic-Viewer-F)
          --port=N        待ち受けポート (設定より優先)
          --bind=MODE     lan (全インターフェース) / localhost
          --root=DIR      公開フォルダ (設定より優先)
          -v, --verbose   詳細ログ
          -h, --help      このヘルプ

        起動後、設定画面の URL (http://127.0.0.1:PORT/__app/admin.html?token=...) がログに出ます。
        """;

    public static ServerOptions Parse(IEnumerable<string> args)
    {
        var o = new ServerOptions();
        var list = args.ToList();
        if (list.Count > 0 && string.Equals(list[0], "covers", StringComparison.OrdinalIgnoreCase))
        {
            o.Command = "covers";
            list.RemoveAt(0);
        }
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            string? Value(string name)
            {
                if (a.StartsWith(name + "=", StringComparison.Ordinal)) return a[(name.Length + 1)..];
                if (a == name && i + 1 < list.Count) return list[++i];
                return null;
            }

            if (a is "-h" or "--help" or "/?") { o.Help = true; continue; }
            if (a is "-v" or "--verbose") { o.Verbose = true; continue; }
            if (a == "--headless") { o.Headless = true; continue; }
            if (a == "--service") { o.Service = true; o.Headless = true; continue; }
            if (a == "--minimized") { o.Minimized = true; continue; }
            if (a.StartsWith("--config", StringComparison.Ordinal)) { o.ConfigPath = Value("--config"); if (o.ConfigPath == null) o.Errors.Add("--config にはパスが必要です"); continue; }
            if (a.StartsWith("--port", StringComparison.Ordinal))
            {
                var v = Value("--port");
                if (int.TryParse(v, out var p) && p is >= 1 and <= 65535) o.Port = p; else o.Errors.Add($"--port の値が不正です: {v}");
                continue;
            }
            if (a.StartsWith("--bind", StringComparison.Ordinal))
            {
                var v = Value("--bind");
                if (v is "lan" or "localhost") o.Bind = v; else o.Errors.Add($"--bind は lan か localhost です: {v}");
                continue;
            }
            if (a.StartsWith("--root", StringComparison.Ordinal)) { o.Root = Value("--root"); if (o.Root == null) o.Errors.Add("--root にはフォルダが必要です"); continue; }
            if (o.Command == "covers") { o.Rest.Add(a); continue; }
            if (!a.StartsWith('-')) { o.Positional.Add(a); continue; }
            o.Errors.Add($"不明なオプションです: {a}");
        }
        return o;
    }

    public void ApplyOverrides(LibraryConfig cfg)
    {
        if (Port != null) cfg.Port = Port.Value;
        if (Bind != null) cfg.Bind = Bind;
        if (Root != null) cfg.Root = Root;
        cfg.Normalize();
    }
}
