using System.Threading.Channels;
using LibraryServer.Core.Config;
using LibraryServer.Core.Covers;

namespace LibraryServer.Server;

/// <summary>表紙生成の現在状況 (設定画面 / トレイ用のスナップショット)</summary>
public sealed record CoverStatusSnapshot(
    bool Running,
    string Phase,
    string? Current,
    int Index,
    int Total,
    int Made,
    int Skipped,
    int Failed,
    DateTimeOffset? StartedAt,
    CoverSummary? LastSummary,
    string? LastError,
    IReadOnlyList<CoverEvent> Recent,
    int QueueLength);

/// <summary>
/// 表紙サイドカーの自動生成:
///   - 起動時 (covers.auto のとき) に「元ファイルより古い / 無い」表紙だけ作る
///   - フォルダ監視 (IndexService.FileChanged) で追加・更新されたファイルの表紙を 1 件ずつ作る。
///     コピー途中のファイルを掴まないよう、サイズが 2 秒間変わらず読み取りで開けるまで待つ
///   - 設定画面からの手動実行 (StartRun) も同じキューで直列化する
/// </summary>
public sealed class CoverAutoService : IHostedService, IDisposable
{
    private const int RecentLimit = 200;

    private readonly LibraryRuntime _rt;
    private readonly ILogger<CoverAutoService> _log;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly LinkedList<CoverEvent> _recent = new();

    private CancellationTokenSource? _lifetime;
    private Task? _consumer;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    private bool _running;
    private string _phase = "idle";
    private string? _current;
    private int _index, _total, _made, _skipped, _failed;
    private DateTimeOffset? _startedAt;
    private CoverSummary? _lastSummary;
    private string? _lastError;

    /// <summary>1 件処理するたび (SSE / ログ用)</summary>
    public event Action<CoverEvent>? Progress;
    /// <summary>実行の開始・終了 (状態表示の更新用)</summary>
    public event Action? StateChanged;

    public CoverAutoService(LibraryRuntime rt, ILogger<CoverAutoService> log)
    {
        _rt = rt;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime = new CancellationTokenSource();
        _rt.Index.FileChanged += Enqueue;
        _rt.ConfigChanged += OnConfigChanged;
        _consumer = Task.Run(() => ConsumeAsync(_lifetime.Token));
        if (_rt.Config.Covers.Auto && _rt.Config.IsConfigured)
        {
            StartRun(new CoverJob { Stale = true }, "startup");
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _rt.Index.FileChanged -= Enqueue;
        _rt.ConfigChanged -= OnConfigChanged;
        Cancel();
        _lifetime?.Cancel();
        _queue.Writer.TryComplete();
        if (_consumer != null)
        {
            try { await _consumer.WaitAsync(cancellationToken); } catch (Exception) { }
        }
    }

    public CoverStatusSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new CoverStatusSnapshot(_running, _phase, _current, _index, _total, _made, _skipped, _failed,
                _startedAt, _lastSummary, _lastError, _recent.ToList(), _pending.Count);
        }
    }

    /// <summary>一括実行を開始する。既に走っていれば false</summary>
    public bool StartRun(CoverJob job, string phase)
    {
        lock (_lock)
        {
            if (_running) return false;
            _running = true;
            _phase = phase;
            _current = null;
            _index = _total = _made = _skipped = _failed = 0;
            _startedAt = DateTimeOffset.UtcNow;
            _lastError = null;
            _runCts = new CancellationTokenSource();
            var ct = _runCts.Token;
            _runTask = Task.Run(() => RunAsync(job, ct), CancellationToken.None);
        }
        StateChanged?.Invoke();
        return true;
    }

    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_lock) { cts = _runCts; }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task RunAsync(CoverJob job, CancellationToken ct)
    {
        var cfg = _rt.Config;
        var gen = new CoverGenerator(cfg, _log);
        CoverSummary? summary = null;
        string? error = null;
        try
        {
            var problem = gen.Validate();
            if (problem != null) throw new InvalidOperationException(problem);
            var progress = new Progress<CoverEvent>(Record);
            // Progress<T> は同期コンテキスト無しだとスレッドプールに投げるので、順序を保つため直接呼ぶ
            summary = await Task.Run(() => gen.Run(job, new SyncProgress(Record), ct), CancellationToken.None);
            _log.LogInformation("表紙生成 ({Phase}): 生成 {Made} / スキップ {Skipped} / 失敗 {Failed} ({Sec:F1}s){Cancel}",
                _phase, summary.Made, summary.Skipped, summary.Failed, summary.Elapsed.TotalSeconds, summary.Cancelled ? " [中断]" : "");
            GC.SuppressFinalize(progress);
        }
        catch (OperationCanceledException)
        {
            error = "中断しました / cancelled";
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.LogError(ex, "表紙生成に失敗");
        }
        finally
        {
            lock (_lock)
            {
                _running = false;
                _phase = "idle";
                _current = null;
                _lastSummary = summary ?? _lastSummary;
                _lastError = error;
                _runCts?.Dispose();
                _runCts = null;
            }
            StateChanged?.Invoke();
        }
    }

    private sealed class SyncProgress(Action<CoverEvent> cb) : IProgress<CoverEvent>
    {
        public void Report(CoverEvent value) => cb(value);
    }

    private void Record(CoverEvent ev)
    {
        lock (_lock)
        {
            _index = ev.Index;
            _total = ev.Total;
            _current = ev.RelPath;
            switch (ev.Outcome)
            {
                case CoverOutcome.Made or CoverOutcome.Remade or CoverOutcome.DryRun: _made++; break;
                case CoverOutcome.Skipped: _skipped++; break;
                default: _failed++; break;
            }
            if (ev.Outcome != CoverOutcome.Skipped)
            {
                _recent.AddLast(ev);
                while (_recent.Count > RecentLimit) _recent.RemoveFirst();
            }
        }
        if (ev.Outcome == CoverOutcome.Failed) _log.LogWarning("表紙 NG: {Path} — {Msg}", ev.RelPath, ev.Message);
        else if (ev.Outcome != CoverOutcome.Skipped) _log.LogInformation("表紙 {Outcome}: {Path} -> {Msg}", ev.Outcome, ev.RelPath, ev.Message);
        try { Progress?.Invoke(ev); } catch (Exception) { }
    }

    // ------------------------------------------------------------ 監視キュー

    private void Enqueue(string fullPath)
    {
        if (!_rt.Config.Covers.Auto) return;
        lock (_lock)
        {
            if (!_pending.Add(fullPath)) return;
        }
        _queue.Writer.TryWrite(fullPath);
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var path in _queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await HandleChangedFileAsync(path, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "表紙の自動生成に失敗: {Path}", path);
                }
                finally
                {
                    lock (_lock) { _pending.Remove(path); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleChangedFileAsync(string path, CancellationToken ct)
    {
        if (!await WaitUntilStableAsync(path, ct)) return;

        // 一括実行中なら終わるまで待つ (同じファイルを二重に処理しない)
        Task? run;
        lock (_lock) { run = _runTask; }
        if (run != null)
        {
            try { await run.WaitAsync(ct); } catch (Exception) { }
        }

        var cfg = _rt.Config;
        if (!cfg.Covers.Auto || !cfg.IsConfigured) return;
        var rel = RelativeTo(cfg.Root, path);
        if (rel == null) return;
        var gen = new CoverGenerator(cfg, _log);
        if (gen.Validate() != null) return;
        var ev = await Task.Run(() => gen.GenerateOne(path, rel, new CoverJob { Stale = true }), ct);
        Record(ev with { Index = 0, Total = 0 });
    }

    /// <summary>書き込み中 (コピー途中) のファイルを掴まないよう、サイズが安定して開けるまで待つ</summary>
    private static async Task<bool> WaitUntilStableAsync(string path, CancellationToken ct)
    {
        long last = -1;
        var stable = 0;
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000, ct);
            long size;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return false;
                size = fi.Length;
                using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (IOException) { stable = 0; continue; }
            catch (UnauthorizedAccessException) { stable = 0; continue; }
            if (size == last)
            {
                if (++stable >= 2) return true;
            }
            else
            {
                stable = 0;
                last = size;
            }
        }
        return false;
    }

    private static string? RelativeTo(string root, string full)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var f = Path.GetFullPath(full);
        if (!f.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        var rel = f[(rootFull.Length + 1)..].Replace('\\', '/');
        foreach (var seg in rel.Split('/'))
        {
            if (seg.Length == 0 || seg[0] == '.') return null;
        }
        return rel;
    }

    private void OnConfigChanged(LibraryConfig cfg)
    {
        if (cfg.Covers.Auto && cfg.IsConfigured && !Snapshot().Running)
        {
            StartRun(new CoverJob { Stale = true }, "config");
        }
    }

    public void Dispose()
    {
        _lifetime?.Dispose();
        _runCts?.Dispose();
    }
}
