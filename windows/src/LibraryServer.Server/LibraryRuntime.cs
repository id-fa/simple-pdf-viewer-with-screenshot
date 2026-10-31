using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LibraryServer.Core.Config;
using LibraryServer.Core.Covers;
using LibraryServer.Core.Index;

namespace LibraryServer.Server;

/// <summary>
/// サーバー 1 インスタンス分の状態: 現在の設定、索引、表紙縮小。
/// 設定の差し替え (Apply) は Kestrel を止めずに反映できるものだけを扱う。
/// ポート / バインドの変更は RequiresRestart が true になるので呼び出し側がプロセス (ホスト) を作り直す。
/// </summary>
public sealed class LibraryRuntime : IDisposable
{
    private readonly ILogger<LibraryRuntime> _log;
    private LibraryConfig _cfg;

    public ConfigStore Store { get; }
    public IndexService Index { get; }
    public CoverResizer Covers { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>実際にバインドしたポート (再起動が要るかの比較用。ポート 0 指定時は起動後に実値が入る)</summary>
    public int BoundPort { get; set; }
    public bool BoundLocalhostOnly { get; }

    public LibraryRuntime(ConfigStore store, LibraryConfig cfg, ILoggerFactory lf)
    {
        Store = store;
        _cfg = cfg;
        _log = lf.CreateLogger<LibraryRuntime>();
        Index = new IndexService(cfg, lf.CreateLogger<IndexService>());
        Covers = new CoverResizer(Path.Combine(store.DataDirectory, "covercache"), lf.CreateLogger<CoverResizer>());
        BoundPort = cfg.Port;
        BoundLocalhostOnly = cfg.BindLocalhostOnly;
    }

    public LibraryConfig Config => Volatile.Read(ref _cfg);

    /// <summary>Apply で設定が差し替わったとき (新しい設定を渡す)</summary>
    public event Action<LibraryConfig>? ConfigChanged;

    /// <summary>新しい設定を反映する。save なら library.config.json にも書く</summary>
    public void Apply(LibraryConfig cfg, bool save)
    {
        cfg.Normalize();
        if (save) Store.Save(cfg);
        var old = Config;
        Volatile.Write(ref _cfg, cfg);
        if (old.Root != cfg.Root || old.MaxDepth != cfg.MaxDepth || old.MaxEntries != cfg.MaxEntries
            || old.FollowSymlinks != cfg.FollowSymlinks || old.CoverSuffix != cfg.CoverSuffix
            || !old.Exts.SequenceEqual(cfg.Exts) || !old.CoverExts.SequenceEqual(cfg.CoverExts))
        {
            Index.Reconfigure(cfg);
            _log.LogInformation("設定を反映: root={Root}", cfg.Root);
        }
        try { ConfigChanged?.Invoke(cfg); } catch (Exception ex) { _log.LogWarning(ex, "ConfigChanged handler failed"); }
    }

    public bool RequiresRestart(LibraryConfig cfg) => cfg.Port != BoundPort || cfg.BindLocalhostOnly != BoundLocalhostOnly;

    /// <summary>ブラウザ / スマホから開く URL の候補</summary>
    public IReadOnlyList<string> Urls()
    {
        var port = BoundPort > 0 ? BoundPort : Config.Port;
        var list = new List<string> { $"http://127.0.0.1:{port}/" };
        if (!BoundLocalhostOnly)
        {
            foreach (var ip in LanAddresses()) list.Add($"http://{ip}:{port}/");
        }
        return list;
    }

    public static IEnumerable<IPAddress> LanAddresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    var ip = ua.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ip)) continue;
                    var b = ip.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue;        // APIPA
                    result.Add(ip);
                }
            }
        }
        catch (NetworkInformationException) { }
        return result;
    }

    public void Dispose() => Index.Dispose();
}
