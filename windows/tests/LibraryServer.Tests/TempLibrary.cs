using System.Text;
using LibraryServer.Core.Config;

namespace LibraryServer.Tests;

/// <summary>テスト用の一時ライブラリフォルダ。Dispose で消す</summary>
public sealed class TempLibrary : IDisposable
{
    public string Root { get; }

    public TempLibrary()
    {
        Root = Path.Combine(Path.GetTempPath(), "LibraryServerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Write(string rel, byte[] bytes, DateTime? mtimeUtc = null)
    {
        var full = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        if (mtimeUtc != null) File.SetLastWriteTimeUtc(full, mtimeUtc.Value);
        return full;
    }

    public string Write(string rel, string text) => Write(rel, Encoding.UTF8.GetBytes(text));

    public static byte[] Bytes(int n)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[i] = (byte)(i % 251);
        return b;
    }

    /// <summary>1x1 の PNG (縮小判定で「元が小さい」側に入る)</summary>
    public static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    public LibraryConfig Config(Action<LibraryConfig>? mutate = null)
    {
        var cfg = new LibraryConfig { Root = Root, Port = 0 };
        mutate?.Invoke(cfg);
        return cfg.Normalize();
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}
