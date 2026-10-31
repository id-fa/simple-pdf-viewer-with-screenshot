using LibraryServer.Core.Config;
using Microsoft.Extensions.Logging;

namespace LibraryServer.Core.Index;

/// <summary>
/// 索引 (IndexSnapshot) を保持し、FileSystemWatcher の変更通知と TTL で再構築する。
/// tree / file / cover はすべてここから最新のスナップショットを得る。
///
/// - 初回は要求時に同期的に構築する (library.php がリクエストごとに走査していたのと同じコスト)
/// - 以降は変更通知をデバウンスしてバックグラウンドで作り直し、できたものと差し替える
/// - 監視が効かない環境 (一部のネットワークドライブ) のために StaleAfter を過ぎた要求では
///   古いスナップショットを返しつつ裏で再構築する
/// </summary>
public sealed class IndexService : IDisposable
{
    private readonly ILogger _log;
    private readonly object _lock = new();
    private LibraryConfig _cfg;
    private IndexSnapshot? _snapshot;
    private Task<IndexSnapshot>? _building;
    private bool _dirty;
    private FileSystemWatcher? _watcher;
    private readonly Timer _debounce;
    private bool _disposed;

    public TimeSpan DebounceDelay { get; set; } = TimeSpan.FromSeconds(1.5);
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>スナップショットが差し替わったとき</summary>
    public event Action<IndexSnapshot>? Updated;

    /// <summary>対象拡張子のファイルが作成 / 変更 / リネームされたとき (表紙の自動生成用)。引数は実パス</summary>
    public event Action<string>? FileChanged;

    public IndexService(LibraryConfig cfg, ILogger<IndexService> log)
    {
        _cfg = cfg;
        _log = log;
        _debounce = new Timer(_ => OnDebounceElapsed(), null, Timeout.Infinite, Timeout.Infinite);
        StartWatcher();
    }

    public LibraryConfig Config => _cfg;

    public IndexSnapshot? Current => Volatile.Read(ref _snapshot);

    /// <summary>設定が変わったら呼ぶ。索引を捨てて監視を張り直す</summary>
    public void Reconfigure(LibraryConfig cfg)
    {
        lock (_lock)
        {
            _cfg = cfg;
            _snapshot = null;
            _dirty = false;
        }
        StartWatcher();
    }

    /// <summary>最新の索引を返す。未構築なら構築を待つ。</summary>
    public Task<IndexSnapshot> GetAsync(CancellationToken ct = default)
    {
        var snap = Current;
        if (snap != null)
        {
            if (DateTimeOffset.UtcNow - snap.Generated > StaleAfter) _ = RebuildAsync();
            return Task.FromResult(snap);
        }
        return RebuildAsync().WaitAsync(ct);
    }

    /// <summary>再構築する。進行中なら同じ Task を返し、終わったら必要ならもう一度走る</summary>
    public Task<IndexSnapshot> RebuildAsync()
    {
        lock (_lock)
        {
            if (_building != null)
            {
                _dirty = true;
                return _building;
            }
            var cfg = _cfg;
            _building = Task.Run(() => BuildOnce(cfg));
            return _building;
        }
    }

    /// <summary>変更があったことにして、デバウンス後に再構築する</summary>
    public void MarkDirty()
    {
        if (_disposed) return;
        try { _debounce.Change(DebounceDelay, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    private IndexSnapshot BuildOnce(LibraryConfig cfg)
    {
        IndexSnapshot snap;
        try
        {
            snap = LibraryIndexBuilder.Build(cfg);
            _log.LogInformation("索引を構築: {Count} 件 ({Ms} ms){Trunc}", snap.Entries.Count, (int)snap.BuildDuration.TotalMilliseconds, snap.Truncated ? " [truncated]" : "");
        }
        catch (Exception ex)
        {
            lock (_lock) { _building = null; }
            _log.LogError(ex, "索引の構築に失敗");
            throw;
        }

        bool again;
        lock (_lock)
        {
            if (ReferenceEquals(cfg, _cfg)) _snapshot = snap;   // 構築中に設定が変わっていたら捨てる
            _building = null;
            again = _dirty;
            _dirty = false;
        }
        try { Updated?.Invoke(snap); } catch (Exception ex) { _log.LogWarning(ex, "Updated handler failed"); }
        if (again) _ = RebuildAsync();
        return snap;
    }

    private void OnDebounceElapsed()
    {
        if (_disposed) return;
        _ = RebuildAsync().ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void StartWatcher()
    {
        StopWatcher();
        var cfg = _cfg;
        if (!cfg.IsConfigured || !Directory.Exists(cfg.Root)) return;
        try
        {
            var w = new FileSystemWatcher(cfg.Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += OnFsEvent;
            w.Deleted += OnFsEvent;
            w.Changed += OnFsEvent;
            w.Renamed += OnFsEvent;
            w.Error += (_, e) =>
            {
                _log.LogWarning(e.GetException(), "FileSystemWatcher error; 再走査します");
                MarkDirty();
            };
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "フォルダ監視を開始できません (TTL {Min} 分で再走査します)", StaleAfter.TotalMinutes);
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        // 自分が書く表紙の一時ファイル (.covertmp_*) や無関係な拡張子でも索引は作り直す必要がある
        // (フォルダの出現・消滅、表紙サイドカーの追加で cover フラグが変わる)。デバウンスで吸収する
        MarkDirty();
        if (e.ChangeType is WatcherChangeTypes.Created or WatcherChangeTypes.Changed or WatcherChangeTypes.Renamed)
        {
            var name = Path.GetFileName(e.FullPath);
            if (name.Length > 0 && name[0] != '.' && _cfg.ExtSet().Contains(LibraryIndexBuilder.ExtOf(name)))
            {
                try { FileChanged?.Invoke(e.FullPath); } catch (Exception ex) { _log.LogWarning(ex, "FileChanged handler failed"); }
            }
        }
    }

    private void StopWatcher()
    {
        var w = _watcher;
        _watcher = null;
        if (w == null) return;
        try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
    }

    public void Dispose()
    {
        _disposed = true;
        StopWatcher();
        _debounce.Dispose();
    }
}
