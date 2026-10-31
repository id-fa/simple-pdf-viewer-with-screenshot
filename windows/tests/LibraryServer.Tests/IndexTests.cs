using LibraryServer.Core.Index;
using Xunit;

namespace LibraryServer.Tests;

public class PathKeysTests
{
    [Theory]
    [InlineData("a/b.cbz", "a/b.cbz")]
    [InlineData("\\a\\b.cbz\\", "a/b.cbz")]
    [InlineData("/a/b.cbz", "a/b.cbz")]
    public void Normalizes_separators(string input, string expected)
    {
        Assert.True(PathKeys.TryNormalize(input, out var key));
        Assert.Equal(expected, key);
    }

    [Fact]
    public void Nfd_becomes_nfc()
    {
        var nfd = "が.cbz";          // か + 結合濁点
        Assert.True(PathKeys.TryNormalize(nfd, out var key));
        Assert.Equal("が.cbz", key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("a\0b")]
    public void Rejects_empty_and_nul(string input)
    {
        Assert.False(PathKeys.TryNormalize(input, out _));
    }
}

public class IndexBuilderTests
{
    [Fact]
    public void Walks_files_skips_dots_and_unknown_exts_and_finds_covers()
    {
        using var lib = new TempLibrary();
        lib.Write("a.cbz", TempLibrary.Bytes(10));
        lib.Write("a.cbz.coverimage.webp", TempLibrary.TinyPng);
        lib.Write("Sub/b.pdf", TempLibrary.Bytes(20));
        lib.Write("Sub/B.PDF.CoverImage.PNG", TempLibrary.TinyPng);   // 大文字小文字違いでも拾う
        lib.Write(".hidden/c.cbz", TempLibrary.Bytes(5));
        lib.Write("Sub/.d.cbz", TempLibrary.Bytes(5));
        lib.Write("notes.txt", "x");

        var snap = LibraryIndexBuilder.Build(lib.Config());

        var paths = snap.Entries.Select(e => e.RelPath).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(["Sub/b.pdf", "a.cbz"], paths);
        Assert.True(snap.Find("a.cbz")!.HasCover);
        Assert.True(snap.Find("sub/B.PDF")!.HasCover);              // 要求側の大小も無視
        Assert.EndsWith("B.PDF.CoverImage.PNG", snap.Find("Sub/b.pdf")!.CoverFullPath);
        Assert.Null(snap.Find(".hidden/c.cbz"));
        Assert.Null(snap.Find("../a.cbz"));
        Assert.Null(snap.Find("notes.txt"));
        Assert.False(snap.Truncated);
        Assert.Empty(snap.Warnings);
    }

    [Fact]
    public void Truncates_at_max_entries()
    {
        using var lib = new TempLibrary();
        for (var i = 0; i < 5; i++) lib.Write($"v{i}.cbz", TempLibrary.Bytes(1));
        var snap = LibraryIndexBuilder.Build(lib.Config(c => c.MaxEntries = 3));
        Assert.Equal(3, snap.Entries.Count);
        Assert.True(snap.Truncated);
        Assert.Single(snap.Warnings);
    }

    [Fact]
    public void Respects_max_depth()
    {
        using var lib = new TempLibrary();
        lib.Write("top.cbz", TempLibrary.Bytes(1));
        lib.Write("d1/one.cbz", TempLibrary.Bytes(1));
        lib.Write("d1/d2/two.cbz", TempLibrary.Bytes(1));
        var snap = LibraryIndexBuilder.Build(lib.Config(c => c.MaxDepth = 1));
        Assert.Equal(["d1/one.cbz", "top.cbz"], snap.Entries.Select(e => e.RelPath).OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void Nfd_file_name_is_served_as_nfc_and_found_either_way()
    {
        using var lib = new TempLibrary();
        var nfd = "が.cbz";
        lib.Write(nfd, TempLibrary.Bytes(3));
        var snap = LibraryIndexBuilder.Build(lib.Config());
        var e = Assert.Single(snap.Entries);
        Assert.Equal("が.cbz", e.RelPath);
        Assert.NotNull(snap.Find("が.cbz"));
        Assert.NotNull(snap.Find(nfd));
    }

    [Fact]
    public void Size_and_mtime_are_reported()
    {
        using var lib = new TempLibrary();
        var when = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        lib.Write("x.pdf", TempLibrary.Bytes(1234), when);
        var e = Assert.Single(LibraryIndexBuilder.Build(lib.Config()).Entries);
        Assert.Equal(1234, e.Size);
        Assert.Equal(new DateTimeOffset(when).ToUnixTimeSeconds(), e.Mtime);
    }
}
