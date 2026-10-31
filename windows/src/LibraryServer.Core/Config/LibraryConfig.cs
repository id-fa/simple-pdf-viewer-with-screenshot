using System.Text.Json;
using System.Text.Json.Serialization;

namespace LibraryServer.Core.Config;

/// <summary>
/// library.config.json の内容。キー名と既定値は library.php / library.config.example.php と同じ
/// (camelCase)。fsEncoding は .NET が常に Unicode でファイル名を扱うので存在しない。
/// </summary>
public sealed class LibraryConfig
{
    public const int DefaultPort = 8321;

    /// <summary>公開するフォルダの絶対パス。空なら「未設定」(API は 503 を返す)</summary>
    public string Root { get; set; } = "";

    /// <summary>一覧のルート表示名。空なら root のフォルダ名</summary>
    public string Label { get; set; } = "";

    /// <summary>Basic 認証。null なら認証なし</summary>
    public AuthConfig? Auth { get; set; }

    public List<string> Exts { get; set; } = ["pdf", "cbz", "cbr", "cb7", "epub", "zip", "rar", "7z"];

    public string CoverSuffix { get; set; } = ".coverimage";

    public List<string> CoverExts { get; set; } = ["webp", "avif", "png", "jpg", "jpeg", "gif"];

    public int MaxEntries { get; set; } = 5000;

    public int MaxDepth { get; set; } = 12;

    public bool FollowSymlinks { get; set; }

    // ---- ここから Windows アプリ固有 ----

    /// <summary>待ち受けポート</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>"lan" = 全インターフェース (0.0.0.0 / [::]) / "localhost" = ループバックのみ</summary>
    public string Bind { get; set; } = "lan";

    /// <summary>全レスポンスに COOP / COEP / CORP を付ける (wasm-vips の crossOriginIsolated 用)</summary>
    public bool Coi { get; set; }

    /// <summary>表紙生成の設定 (tools/generate_coverimages.* の coverTool と同じキー)</summary>
    public CoverConfig Covers { get; set; } = new();

    /// <summary>Windows ログオン時にトレイアプリを起動する</summary>
    public bool AutoStart { get; set; } = true;

    // ---- 導出値 ----

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Root);

    [JsonIgnore]
    public bool BindLocalhostOnly => string.Equals(Bind, "localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>ping / tree の root 表示名 (label か root のフォルダ名)</summary>
    public string DisplayLabel()
    {
        if (!string.IsNullOrEmpty(Label)) return Label;
        var trimmed = Root.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    /// <summary>
    /// library.php が設定読み込み直後に行う正規化と同じ: 拡張子を小文字化、coverSuffix から
    /// パス区切りと NUL を除去、数値の下限を保証する。元のインスタンスを書き換えて返す。
    /// </summary>
    public LibraryConfig Normalize()
    {
        Root = (Root ?? "").Trim();
        Label = Label ?? "";
        Exts = NormalizeExts(Exts);
        CoverExts = NormalizeExts(CoverExts);
        CoverSuffix = (CoverSuffix ?? "").Replace("/", "").Replace("\\", "").Replace("\0", "");
        if (MaxEntries < 1) MaxEntries = 1;
        if (MaxDepth < 0) MaxDepth = 0;
        if (Port < 1 || Port > 65535) Port = DefaultPort;
        Bind = BindLocalhostOnly ? "localhost" : "lan";
        if (Auth != null && (string.IsNullOrEmpty(Auth.User) || string.IsNullOrEmpty(Auth.Pass))) Auth = null;
        Covers ??= new CoverConfig();
        Covers.Normalize();
        return this;
    }

    private static List<string> NormalizeExts(IEnumerable<string>? exts)
    {
        var list = new List<string>();
        foreach (var e in exts ?? [])
        {
            var s = (e ?? "").Trim().TrimStart('.').ToLowerInvariant();
            if (s.Length > 0 && !list.Contains(s)) list.Add(s);
        }
        return list;
    }

    public HashSet<string> ExtSet() => new(Exts, StringComparer.Ordinal);

    public LibraryConfig Clone() => JsonSerializer.Deserialize<LibraryConfig>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!.Normalize();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

public sealed class AuthConfig
{
    public string User { get; set; } = "";

    /// <summary>平文、または bcrypt ハッシュ ("$2a$" / "$2y$" 始まり)。PHP の password_hash() 出力と互換</summary>
    public string Pass { get; set; } = "";

    public string Realm { get; set; } = "Library";
}

public sealed class CoverConfig
{
    /// <summary>起動時と新規ファイル検知時に自動生成する</summary>
    public bool Auto { get; set; } = true;

    /// <summary>webp | jpeg | png</summary>
    public string Format { get; set; } = "webp";

    public int Quality { get; set; } = 82;

    /// <summary>これを超える画像だけ縮小する (0 で無制限)</summary>
    public int MaxWidth { get; set; } = 1200;

    public int MaxHeight { get; set; } = 1600;

    /// <summary>書庫内のファイル名順: lexical | natural</summary>
    public string Sort { get; set; } = "lexical";

    /// <summary>EPUB の表紙: spine (読み順の 1 枚目) | metadata (OPF の cover-image 優先)</summary>
    public string EpubCover { get; set; } = "spine";

    /// <summary>表紙の更新日時を抽出元ファイルに揃える</summary>
    public bool MatchMtime { get; set; }

    public int PdfDpi { get; set; } = 150;

    /// <summary>先頭候補がデコードできないとき次を試す上限</summary>
    public int MaxCandidates { get; set; } = 5;

    /// <summary>縮小不要なら再エンコードせず書庫内の原本をそのまま置く</summary>
    public bool Passthrough { get; set; }

    public void Normalize()
    {
        Format = (Format ?? "webp").Trim().ToLowerInvariant();
        if (Format == "jpg") Format = "jpeg";
        if (Format is not ("webp" or "jpeg" or "png")) Format = "webp";
        Quality = Math.Clamp(Quality, 1, 100);
        if (MaxWidth < 0) MaxWidth = 0;
        if (MaxHeight < 0) MaxHeight = 0;
        Sort = string.Equals(Sort, "natural", StringComparison.OrdinalIgnoreCase) ? "natural" : "lexical";
        EpubCover = string.Equals(EpubCover, "metadata", StringComparison.OrdinalIgnoreCase) ? "metadata" : "spine";
        PdfDpi = Math.Clamp(PdfDpi, 36, 600);
        MaxCandidates = Math.Clamp(MaxCandidates, 1, 50);
    }

    /// <summary>出力ファイルの拡張子 (coverExts に含まれている必要がある)</summary>
    public string OutputExt => Format == "jpeg" ? "jpg" : Format;
}
