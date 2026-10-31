using System.Collections.Concurrent;

namespace LibraryServer.Server;

/// <summary>
/// root の外にあるローカルファイルを、ビューアに一時 URL (/__local/{token}) で渡すための台帳。
/// トレイアプリのファイル関連付け (LibraryServer.exe D:\x\book.cbz) で使う。
/// トークンは推測できない乱数、有効期限付き、配信はループバックからのみ。
/// </summary>
public sealed class LocalFileRegistry
{
    private sealed record Entry(string Path, DateTimeOffset Expires);

    private readonly ConcurrentDictionary<string, Entry> _map = new();

    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromMinutes(30);

    public string Register(string fullPath, TimeSpan? ttl = null)
    {
        Purge();
        var token = AdminToken.Generate();
        _map[token] = new Entry(System.IO.Path.GetFullPath(fullPath), DateTimeOffset.UtcNow + (ttl ?? DefaultTtl));
        return token;
    }

    public string? Resolve(string token)
    {
        if (!_map.TryGetValue(token, out var e)) return null;
        if (e.Expires < DateTimeOffset.UtcNow)
        {
            _map.TryRemove(token, out _);
            return null;
        }
        return e.Path;
    }

    private void Purge()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _map)
        {
            if (kv.Value.Expires < now) _map.TryRemove(kv.Key, out _);
        }
    }
}
