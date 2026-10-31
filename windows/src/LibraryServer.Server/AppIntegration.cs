using System.Security.Cryptography;

namespace LibraryServer.Server;

/// <summary>
/// 設定画面 (admin.html) からトレイアプリにしか出来ない操作を頼むための口。
/// ヘッドレス / サービスでは NullAppIntegration が「対応していない」と答える。
/// </summary>
public interface IAppIntegration
{
    bool IsApp { get; }

    /// <summary>フォルダ選択ダイアログを出して選ばれたパスを返す (キャンセルは null)</summary>
    Task<string?> PickFolderAsync(string? initial);

    bool CanRestart { get; }

    /// <summary>ポート / バインドの変更を反映するためにサーバーを作り直す</summary>
    Task RestartAsync();

    /// <summary>Windows ログオン時の自動起動 (null = この環境では設定できない)</summary>
    bool? AutoStartEnabled { get; }

    void SetAutoStart(bool enabled);

    /// <summary>トレイアプリ自身を終了する</summary>
    void Exit();
}

public sealed class NullAppIntegration : IAppIntegration
{
    public bool IsApp => false;
    public Task<string?> PickFolderAsync(string? initial) => Task.FromResult<string?>(null);
    public bool CanRestart => false;
    public Task RestartAsync() => Task.CompletedTask;
    public bool? AutoStartEnabled => null;
    public void SetAutoStart(bool enabled) { }
    public void Exit() { }
}

/// <summary>
/// 管理 API (/__admin/*) の合言葉。起動ごとに乱数で作り、ログと起動時のコンソールに URL ごと出す。
/// 同一 PC 上の別サイトが localhost を叩く CSRF を防ぐためのもので、LAN からは loopback 判定で弾く。
/// </summary>
public sealed class AdminToken
{
    public string Value { get; }

    public AdminToken(string? fixedValue = null)
    {
        Value = string.IsNullOrEmpty(fixedValue) ? Generate() : fixedValue;
    }

    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public bool Matches(string? candidate) =>
        !string.IsNullOrEmpty(candidate)
        && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(candidate), System.Text.Encoding.UTF8.GetBytes(Value));
}
