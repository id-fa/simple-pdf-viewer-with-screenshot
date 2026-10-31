using System.Diagnostics;
using LibraryServer.Core.Config;
using LibraryServer.Core.Index;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace LibraryServer.Core.Covers;

/// <summary>1 回の生成ジョブの指定 (tools/generate_coverimages.* のコマンドラインに対応)</summary>
public sealed class CoverJob
{
    /// <summary>既存の表紙があっても作り直す</summary>
    public bool Force { get; set; }
    /// <summary>表紙が元ファイルより古いときだけ作り直す</summary>
    public bool Stale { get; set; }
    public bool DryRun { get; set; }
    /// <summary>相対パスにこの文字列を含むものだけ (大小無視)</summary>
    public string? Filter { get; set; }
    /// <summary>root 配下のサブフォルダだけ</summary>
    public string? SubPath { get; set; }
    /// <summary>対象拡張子を絞る (null で設定の exts 全部)</summary>
    public HashSet<string>? OnlyExts { get; set; }
    /// <summary>最大 N 件だけ生成 (0 で無制限)</summary>
    public int Limit { get; set; }
}

public enum CoverOutcome { Made, Remade, Skipped, Failed, DryRun }

public sealed record CoverEvent(CoverOutcome Outcome, string RelPath, string Message, int Index, int Total, string? Source = null);

public sealed record CoverSummary(int Made, int Skipped, int Failed, int Total, TimeSpan Elapsed, bool Cancelled);

/// <summary>
/// 表紙サイドカーの生成。表紙の決め方・命名・スキップ条件・原子的書き込みは
/// tools/generate_coverimages.php / .py と同じ (片方を直したらこちらも追うこと):
///   PDF  … 1 ページ目を PDFium で描画
///   EPUB … 構造解析 (EpubAnalyzer) で読み順の 1 枚目。取れなければ manifest 順 → ファイル名順
///   書庫 … ファイル名順 (lexical = バイト列順 / natural) の 1 ファイル目
/// </summary>
public sealed class CoverGenerator
{
    private readonly LibraryConfig _cfg;
    private readonly ILogger _log;

    public CoverGenerator(LibraryConfig cfg, ILogger log)
    {
        _cfg = cfg;
        _log = log;
    }

    public CoverConfig Options => _cfg.Covers;

    /// <summary>出力拡張子が coverExts に無いと library.php / 本サーバーが表紙として認識できないので起動時に弾く</summary>
    public string? Validate()
    {
        var ext = _cfg.Covers.OutputExt;
        if (!_cfg.CoverExts.Contains(ext))
        {
            return $"出力拡張子 .{ext} が coverExts に含まれていません。format を変えるか coverExts に追加してください。"
                 + $" / The output extension .{ext} is not in coverExts.";
        }
        if (!_cfg.IsConfigured) return "root が設定されていません / 'root' is not set";
        if (!Directory.Exists(_cfg.Root)) return "root フォルダが見つかりません / The root folder was not found: " + _cfg.Root;
        return null;
    }

    /// <summary>対象ファイルを列挙する (索引の走査を流用。件数上限は無し)</summary>
    public List<LibraryEntry> Targets(CoverJob job)
    {
        var cfg = _cfg.Clone();
        cfg.MaxEntries = int.MaxValue;
        var snap = LibraryIndexBuilder.Build(cfg);
        IEnumerable<LibraryEntry> q = snap.Entries;
        if (!string.IsNullOrEmpty(job.SubPath))
        {
            if (!PathKeys.TryNormalize(job.SubPath, out var sub)) throw new ArgumentException("--path が不正です / invalid --path");
            q = q.Where(e => e.RelPath.StartsWith(sub + "/", StringComparison.OrdinalIgnoreCase));
        }
        if (job.OnlyExts != null) q = q.Where(e => job.OnlyExts.Contains(e.Ext));
        if (!string.IsNullOrEmpty(job.Filter)) q = q.Where(e => e.RelPath.Contains(job.Filter, StringComparison.OrdinalIgnoreCase));
        return q.OrderBy(e => e.RelPath, StringComparer.Ordinal).ToList();
    }

    /// <summary>一括生成。進捗は progress に 1 件ずつ流す</summary>
    public CoverSummary Run(CoverJob job, IProgress<CoverEvent>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var targets = Targets(job);
        int made = 0, skipped = 0, failed = 0, index = 0;
        var cancelled = false;
        foreach (var e in targets)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            if (job.Limit > 0 && made >= job.Limit) break;
            index++;
            var ev = GenerateOne(e.FullPath, e.RelPath, job, index, targets.Count);
            progress?.Report(ev);
            switch (ev.Outcome)
            {
                case CoverOutcome.Made or CoverOutcome.Remade or CoverOutcome.DryRun: made++; break;
                case CoverOutcome.Skipped: skipped++; break;
                default: failed++; break;
            }
        }
        return new CoverSummary(made, skipped, failed, targets.Count, sw.Elapsed, cancelled);
    }

    /// <summary>1 ファイル分。自動生成 (監視) からも呼ぶ</summary>
    public CoverEvent GenerateOne(string fullPath, string relPath, CoverJob job, int index = 0, int total = 0)
    {
        var cov = _cfg.Covers;
        var directory = Path.GetDirectoryName(fullPath)!;
        var name = Path.GetFileName(fullPath);
        var ext = LibraryIndexBuilder.ExtOf(name);
        var outExt = cov.OutputExt;

        DateTime srcMtime;
        try { srcMtime = File.GetLastWriteTimeUtc(fullPath); } catch (Exception) { srcMtime = DateTime.MinValue; }

        // --- 既存の表紙 (どの拡張子でも) を探す ---
        var existing = LibraryIndexBuilder.FindCover(directory, name, _cfg, null);
        if (existing != null && !job.Force)
        {
            var needs = job.Stale && srcMtime != DateTime.MinValue && File.GetLastWriteTimeUtc(existing) < srcMtime;
            if (!needs)
            {
                // mtime 同期だけは既存ファイルにも当てておく (後から matchMtime を付けたケース)
                if (cov.MatchMtime && srcMtime != DateTime.MinValue && !job.DryRun && File.GetLastWriteTimeUtc(existing) != srcMtime)
                {
                    try { File.SetLastWriteTimeUtc(existing, srcMtime); } catch (Exception) { }
                }
                return new CoverEvent(CoverOutcome.Skipped, relPath, Path.GetFileName(existing), index, total);
            }
        }

        var (picked, err) = ExtractCover(fullPath, ext);
        if (picked == null) return new CoverEvent(CoverOutcome.Failed, relPath, err ?? "不明なエラー / unknown error", index, total);

        byte[] data;
        var writeExt = outExt;
        string dim;
        try
        {
            if (picked.Image != null)
            {
                using (picked.Image)
                {
                    var enc = ImageOps.Encode(picked.Image, cov.Format, cov.Quality, cov.MaxWidth, cov.MaxHeight);
                    data = enc.Data;
                    dim = $"{enc.Width}x{enc.Height}";
                }
            }
            else
            {
                var raw = picked.Data!;
                var size = ImageOps.Probe(raw);
                var needResize = size != null && ((cov.MaxWidth > 0 && size.Value.Width > cov.MaxWidth) || (cov.MaxHeight > 0 && size.Value.Height > cov.MaxHeight));
                var canPassthrough = picked.Ext != null && _cfg.CoverExts.Contains(picked.Ext) && !needResize;
                if (cov.Passthrough && canPassthrough)
                {
                    data = raw;
                    writeExt = picked.Ext!;
                    dim = size is { } s0 ? $"{s0.Width}x{s0.Height}" : "?";
                }
                else
                {
                    using var bmp = ImageOps.Decode(raw);
                    if (bmp != null)
                    {
                        var enc = ImageOps.Encode(bmp, cov.Format, cov.Quality, cov.MaxWidth, cov.MaxHeight);
                        data = enc.Data;
                        dim = $"{enc.Width}x{enc.Height}";
                    }
                    else if (canPassthrough)
                    {
                        // デコードできない形式 (jxl / heic / avif 等) でも、そのまま置ける形式なら原本を使う
                        data = raw;
                        writeExt = picked.Ext!;
                        dim = size is { } s1 ? $"{s1.Width}x{s1.Height}" : "?";
                    }
                    else
                    {
                        return new CoverEvent(CoverOutcome.Failed, relPath, "画像を変換できません / cannot convert the image: " + picked.Source, index, total);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return new CoverEvent(CoverOutcome.Failed, relPath, "画像を変換できません / cannot convert the image: " + ex.Message, index, total);
        }

        var coverName = name + _cfg.CoverSuffix + "." + writeExt;
        var coverPath = Path.Combine(directory, coverName);
        var outcome = existing != null ? CoverOutcome.Remade : CoverOutcome.Made;
        var msg = $"{coverName} ({dim}, {HumanSize(data.Length)})";

        if (job.DryRun) return new CoverEvent(CoverOutcome.DryRun, relPath, msg, index, total, picked.Source);

        // 同じフォルダの一時ファイルに書いてから置き換える (途中で落ちても半端な表紙を残さない)。
        // '.' 始まりなので一覧の走査にも引っかからない
        var tmp = Path.Combine(directory, $".covertmp_{Environment.ProcessId}_{index}_{Environment.CurrentManagedThreadId}");
        try
        {
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, coverPath, overwrite: true);
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch (Exception) { }
            return new CoverEvent(CoverOutcome.Failed, relPath, "書き込めません / cannot write: " + ex.Message, index, total);
        }

        // 別拡張子の古い表紙が残っていると coverExts の優先順で拾われてしまうので消す
        foreach (var ce in _cfg.CoverExts)
        {
            if (ce == writeExt) continue;
            var old = Path.Combine(directory, name + _cfg.CoverSuffix + "." + ce);
            if (File.Exists(old))
            {
                try { File.Delete(old); } catch (Exception) { }
            }
        }

        // 表紙の更新日時を「抽出元ファイル自体」に合わせる (書庫内画像の日時ではない)
        if (cov.MatchMtime && srcMtime != DateTime.MinValue)
        {
            try { File.SetLastWriteTimeUtc(coverPath, srcMtime); } catch (Exception) { }
        }
        return new CoverEvent(outcome, relPath, msg, index, total, picked.Source);
    }

    // ---------------------------------------------------------------- 抽出

    public sealed class Picked : IDisposable
    {
        /// <summary>書庫から取り出した原本バイト列</summary>
        public byte[]? Data { get; init; }
        /// <summary>PDF のレンダリング結果</summary>
        public SKBitmap? Image { get; init; }
        /// <summary>原本の拡張子 (PDF 由来なら null)</summary>
        public string? Ext { get; init; }
        public required string Source { get; init; }
        public void Dispose() => Image?.Dispose();
    }

    public (Picked? Picked, string? Error) ExtractCover(string fullPath, string ext)
    {
        var cov = _cfg.Covers;
        if (ext == "pdf")
        {
            var bmp = PdfRenderer.RenderFirstPage(fullPath, cov.PdfDpi, out var perr);
            if (bmp == null) return (null, "PDF: " + (perr ?? "レンダリングできません / cannot render"));
            return (new Picked { Image = bmp, Source = "p.1" }, null);
        }

        using var arc = ArchiveSource.TryOpen(fullPath, out var aerr);
        if (arc == null) return (null, "書庫を開けません / cannot open the archive: " + aerr);

        var images = arc.PageImages().ToList();
        if (images.Count == 0) return (null, "画像ファイルが見つかりません / no image files in the archive");

        var limit = Math.Max(1, cov.MaxCandidates);
        var candidates = new List<ArchiveEntryInfo>();
        var label = $"ファイル名順({cov.Sort})";

        if (ext == "epub")
        {
            var r = EpubAnalyzer.Candidates(arc, images, cov, limit);
            if (r.Candidates.Count > 0)
            {
                candidates.AddRange(r.Candidates);
                label = "EPUB " + (cov.EpubCover == "metadata" ? "metadata/" : "") + "読み順";
                if (r.Note != null) label += $" ({r.Note})";
            }
            else if (r.Note != null)
            {
                _log.LogDebug("EPUB 構造解析: {Note} → ファイル名順にフォールバック: {Path}", r.Note, fullPath);
            }
        }

        // 構造解析で取れなかった / EPUB 以外はファイル名順
        var ordered = cov.Sort == "natural"
            ? images.OrderBy(e => e.Name, NaturalComparer.Instance).ToList()
            : images.OrderBy(e => e.SortKey, Comparer<byte[]>.Create(ArchiveSource.CompareSortKey)).ToList();
        foreach (var e in ordered)
        {
            if (candidates.Count >= limit * 2) break;
            if (!candidates.Contains(e)) candidates.Add(e);
        }

        // 先頭から順に読んで、画像として認識できた最初の 1 枚を採用する
        var tried = 0;
        foreach (var e in candidates)
        {
            if (tried >= limit) break;
            tried++;
            var data = arc.Read(e);
            if (data == null || data.Length == 0) continue;
            if (ImageOps.Probe(data) == null && !LooksLikeImage(data))
            {
                _log.LogDebug("デコードできないので次の候補へ: {Entry}", e.Name);
                continue;
            }
            return (new Picked { Data = data, Ext = LibraryIndexBuilder.ExtOf(e.Name), Source = $"{label} {e.Name}" }, null);
        }
        return (null, $"画像を読み出せません ({tried} 候補を試行) / could not read an image ({tried} candidates tried)");
    }

    /// <summary>Skia が読めない形式 (AVIF / JXL / HEIC) でも、マジックナンバーで画像らしければ候補として通す (passthrough 用)</summary>
    private static bool LooksLikeImage(byte[] d)
    {
        if (d.Length < 12) return false;
        // ISO BMFF (avif / heic): "ftyp" at 4
        if (d[4] == (byte)'f' && d[5] == (byte)'t' && d[6] == (byte)'y' && d[7] == (byte)'p') return true;
        // JXL: FF 0A または 00 00 00 0C 4A 58 4C 20
        if (d[0] == 0xFF && d[1] == 0x0A) return true;
        if (d[0] == 0 && d[1] == 0 && d[2] == 0 && d[3] == 0x0C && d[4] == (byte)'J' && d[5] == (byte)'X' && d[6] == (byte)'L' && d[7] == 0x20) return true;
        return false;
    }

    public static string HumanSize(long n)
    {
        if (n >= 1048576) return $"{n / 1048576.0:F1}MB";
        if (n >= 1024) return $"{n / 1024.0:F0}KB";
        return $"{n}B";
    }
}
