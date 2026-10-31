using System.Text.Json;

namespace LibraryServer.Core.Config;

/// <summary>
/// library.config.json の置き場所の解決と読み書き。
///
/// 探索順:
///   1. 明示されたパス (--config)
///   2. exe と同じフォルダ (ポータブル運用)
///   3. %LocalAppData%\LibraryServer (トレイアプリの既定)
///   4. %ProgramData%\LibraryServer (サービス運用)
/// 見つからなければ 3 (サービスモードでは 4) に新規作成する。
/// </summary>
public sealed class ConfigStore
{
    public const string FileName = "library.config.json";
    public const string AppFolderName = "LibraryServer";

    public string Path { get; }

    public ConfigStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
    }

    public static ConfigStore Resolve(string? explicitPath, bool serviceMode = false)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return new ConfigStore(explicitPath);
        foreach (var cand in Candidates())
        {
            if (File.Exists(cand)) return new ConfigStore(cand);
        }
        return new ConfigStore(serviceMode ? ProgramDataPath() : LocalAppDataPath());
    }

    public static IEnumerable<string> Candidates()
    {
        yield return System.IO.Path.Combine(AppContext.BaseDirectory, FileName);
        yield return LocalAppDataPath();
        yield return ProgramDataPath();
    }

    public static string LocalAppDataPath() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName, FileName);

    public static string ProgramDataPath() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppFolderName, FileName);

    /// <summary>アプリのデータフォルダ (表紙キャッシュ・ログ)。設定ファイルと同じフォルダ</summary>
    public string DataDirectory => System.IO.Path.GetDirectoryName(Path)!;

    public bool Exists => File.Exists(Path);

    /// <summary>存在しなければ既定値 (root 空 = 未設定) を返す。壊れていれば例外</summary>
    public LibraryConfig LoadOrDefault()
    {
        if (!File.Exists(Path)) return new LibraryConfig().Normalize();
        using var fs = File.OpenRead(Path);
        var cfg = JsonSerializer.Deserialize<LibraryConfig>(fs, LibraryConfig.JsonOptions)
                  ?? throw new InvalidDataException($"{Path}: 設定が空です / the configuration is empty");
        return cfg.Normalize();
    }

    /// <summary>一時ファイルに書いてから差し替える (書き込み途中で落ちても元の設定が残る)</summary>
    public void Save(LibraryConfig cfg)
    {
        Directory.CreateDirectory(DataDirectory);
        var tmp = Path + ".tmp";
        using (var fs = File.Create(tmp))
        {
            JsonSerializer.Serialize(fs, cfg, LibraryConfig.JsonOptions);
        }
        File.Move(tmp, Path, overwrite: true);
    }
}
