using System.Text;
using LibraryServer.Core.Config;
using LibraryServer.Core.Covers;
using Microsoft.Extensions.Logging.Abstractions;

namespace LibraryServer.Server;

/// <summary>`LibraryServer.Host covers ...` — tools/generate_coverimages.py と同じオプション・出力の CLI</summary>
public static class CoverCli
{
    public const string Usage = """
        LibraryServer.Host covers [options]   表紙画像 (サイドカー) を一括生成する

        対象の指定:
          --root=DIR            表紙を作るフォルダ (省略時は library.config.json の root)
          --config=PATH         library.config.json の場所
          --path=SUB            root 配下のサブフォルダだけを処理する
          --filter=TEXT         相対パスにこの文字列を含むものだけ (大小無視)
          --ext=pdf,epub        対象拡張子を絞る
          --limit=N             最大 N 件だけ生成する
        生成:
          --format=webp|jpeg|png   出力形式 (既定: webp)
          --quality=N           webp / jpeg の品質 1-100 (既定: 82)
          --max-width=N         最大幅。超える画像だけ縮小。0 で無制限 (既定: 1200)
          --max-height=N        最大高さ (既定: 1600)
          --sort=lexical|natural   書庫内のファイル名順 (既定: lexical)
          --epub-cover=spine|metadata   EPUB の表紙の取り方 (既定: spine)
          --dpi=N               PDF のレンダリング解像度 (既定: 150)
          --passthrough         縮小不要なら再エンコードせず原本をそのまま置く
        上書き / 日時:
          --force               既存の表紙があっても作り直す
          --stale               表紙が元ファイルより古いときだけ作り直す
          --mtime / --no-mtime  表紙の更新日時を抽出元ファイル (PDF/EPUB/書庫自体) に合わせる
        その他:
          --dry-run             書き込まずに何をするか表示する
          --check               使えるバックエンドを表示して終了
          -q, --quiet           生成した項目以外を表示しない
          -v, --verbose         出所も表示する
        """;

    public static int Run(ServerOptions opts)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch (Exception) { }

        var job = new CoverJob();
        bool check = false, quiet = false, help = false;
        bool? mtime = null, passthrough = null;
        string? format = null, sort = null, epubCover = null;
        int? quality = null, maxW = null, maxH = null, dpi = null;
        var errors = new List<string>();

        foreach (var a in opts.Rest)
        {
            string? Val(string name) => a.StartsWith(name + "=", StringComparison.Ordinal) ? a[(name.Length + 1)..] : null;
            int? Int(string name)
            {
                var v = Val(name);
                if (v == null) return null;
                if (int.TryParse(v, out var n)) return n;
                errors.Add($"{name} の値が不正です: {v}");
                return null;
            }
            switch (a)
            {
                case "-h" or "--help": help = true; continue;
                case "--force": job.Force = true; continue;
                case "--stale": job.Stale = true; continue;
                case "--dry-run": job.DryRun = true; continue;
                case "--check": check = true; continue;
                case "-q" or "--quiet": quiet = true; continue;
                case "--mtime": mtime = true; continue;
                case "--no-mtime": mtime = false; continue;
                case "--passthrough": passthrough = true; continue;
            }
            if (Val("--filter") is { } f) { job.Filter = f; continue; }
            if (Val("--path") is { } p) { job.SubPath = p; continue; }
            if (Val("--ext") is { } e)
            {
                job.OnlyExts = e.Split(',').Select(s => s.Trim().TrimStart('.').ToLowerInvariant()).Where(s => s.Length > 0).ToHashSet();
                continue;
            }
            if (a.StartsWith("--limit=", StringComparison.Ordinal)) { job.Limit = Int("--limit") ?? 0; continue; }
            if (Val("--format") is { } fm) { format = fm; continue; }
            if (a.StartsWith("--quality=", StringComparison.Ordinal)) { quality = Int("--quality"); continue; }
            if (a.StartsWith("--max-width=", StringComparison.Ordinal)) { maxW = Int("--max-width"); continue; }
            if (a.StartsWith("--max-height=", StringComparison.Ordinal)) { maxH = Int("--max-height"); continue; }
            if (Val("--sort") is { } so) { sort = so; continue; }
            if (Val("--epub-cover") is { } ec) { epubCover = ec; continue; }
            if (a.StartsWith("--dpi=", StringComparison.Ordinal)) { dpi = Int("--dpi"); continue; }
            errors.Add("不明なオプションです: " + a);
        }
        if (help) { Console.WriteLine(Usage); return 0; }
        if (errors.Count > 0)
        {
            foreach (var e in errors) Console.Error.WriteLine(e);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var store = ConfigStore.Resolve(opts.ConfigPath);
        LibraryConfig cfg;
        try { cfg = store.LoadOrDefault(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"設定ファイルを読めません / Cannot read {store.Path}: {ex.Message}");
            return 1;
        }
        opts.ApplyOverrides(cfg);
        var cov = cfg.Covers;
        if (format != null) cov.Format = format;
        if (quality != null) cov.Quality = quality.Value;
        if (maxW != null) cov.MaxWidth = maxW.Value;
        if (maxH != null) cov.MaxHeight = maxH.Value;
        if (sort != null) cov.Sort = sort;
        if (epubCover != null) cov.EpubCover = epubCover;
        if (dpi != null) cov.PdfDpi = dpi.Value;
        if (mtime != null) cov.MatchMtime = mtime.Value;
        if (passthrough != null) cov.Passthrough = passthrough.Value;
        cfg.Normalize();

        if (check)
        {
            Console.WriteLine($".NET           : {Environment.Version} ({Environment.OSVersion})");
            Console.WriteLine($"SkiaSharp      : {typeof(SkiaSharp.SKBitmap).Assembly.GetName().Version} (webp / jpeg / png 書き出し可)");
            Console.WriteLine($"PDFium         : PDFtoImage {typeof(PDFtoImage.Conversion).Assembly.GetName().Version} (外部コマンド不要)");
            Console.WriteLine($"SharpCompress  : {typeof(SharpCompress.Archives.ArchiveFactory).Assembly.GetName().Version} (zip / cbz / epub / rar / cbr / 7z / cb7)");
            Console.WriteLine($"設定           : {(store.Exists ? store.Path : "(無し — 既定値)")}");
            Console.WriteLine($"root           : {(cfg.IsConfigured ? cfg.Root : "(未設定)")}");
            return 0;
        }

        var gen = new CoverGenerator(cfg, NullLogger.Instance);
        var problem = gen.Validate();
        if (problem != null)
        {
            Console.Error.WriteLine("エラー / Error: " + problem);
            return 1;
        }

        void Info(string s) { if (!quiet) Console.WriteLine(s); }

        List<Core.Index.LibraryEntry> targets;
        try { targets = gen.Targets(job); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("エラー / Error: " + ex.Message);
            return 1;
        }

        Info("root      : " + cfg.Root);
        Info($"対象       : {targets.Count} 件 ({string.Join(", ", job.OnlyExts ?? cfg.ExtSet())})");
        Info($"出力       : {cfg.CoverSuffix}.{cov.OutputExt} / {cov.Format} q{cov.Quality} / 最大 {(cov.MaxWidth > 0 ? cov.MaxWidth.ToString() : "∞")}x{(cov.MaxHeight > 0 ? cov.MaxHeight.ToString() : "∞")}"
             + $" / sort={cov.Sort} / epub={cov.EpubCover}{(cov.MatchMtime ? " / mtime同期" : "")}{(job.DryRun ? " / DRY-RUN" : "")}");
        Info("");

        var progress = new Progress<CoverEvent>();
        var summary = gen.Run(job, new ConsoleProgress(opts.Verbose, quiet), CancellationToken.None);

        Info("");
        Info($"完了: 生成 {summary.Made} / スキップ {summary.Skipped} / 失敗 {summary.Failed}  ({summary.Elapsed.TotalSeconds:F1}s){(job.DryRun ? " ※DRY-RUN" : "")}");
        return summary.Failed > 0 ? 2 : 0;
    }

    private sealed class ConsoleProgress(bool verbose, bool quiet) : IProgress<CoverEvent>
    {
        public void Report(CoverEvent ev)
        {
            switch (ev.Outcome)
            {
                case CoverOutcome.Skipped:
                    if (verbose && !quiet) Console.WriteLine($"  [skip] {ev.RelPath} ({ev.Message})");
                    break;
                case CoverOutcome.Failed:
                    Console.Error.WriteLine($"  [NG]   {ev.RelPath} — {ev.Message}");
                    break;
                case CoverOutcome.DryRun:
                    if (!quiet) Console.WriteLine($"  [dry]  {ev.RelPath} -> {ev.Message} {ev.Source}");
                    break;
                default:
                    Console.WriteLine($"  [{(ev.Outcome == CoverOutcome.Remade ? "再生成" : "生成")}] {ev.RelPath} -> {ev.Message}");
                    if (verbose && ev.Source != null) Console.WriteLine("    出所: " + ev.Source);
                    break;
            }
        }
    }
}
