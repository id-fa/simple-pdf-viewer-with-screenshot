using System.Security.Cryptography;
using System.Text;
using LibraryServer.Core.Config;

namespace LibraryServer.Core.Auth;

/// <summary>library.php の lib_require_auth と同じ規則の Basic 認証</summary>
public static class BasicAuth
{
    public static bool IsEnabled(AuthConfig? auth) =>
        auth != null && !string.IsNullOrEmpty(auth.User) && !string.IsNullOrEmpty(auth.Pass);

    /// <summary>Authorization ヘッダー ("Basic xxx") を検証する。ヘッダーが無ければ false</summary>
    public static bool Verify(AuthConfig auth, string? authorizationHeader)
    {
        var (user, pass) = Parse(authorizationHeader);
        var expected = auth.Pass;
        // '$2y$' 等で始まるならハッシュ (password_hash 済み) とみなす
        var passOk = expected.Length > 3 && expected[0] == '$'
            ? VerifyHash(pass, expected)
            : FixedEquals(expected, pass);
        var userOk = FixedEquals(auth.User, user);
        return userOk & passOk;
    }

    public static (string User, string Pass) Parse(string? header)
    {
        if (string.IsNullOrEmpty(header)) return ("", "");
        header = header.Trim();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return ("", "");
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var i = decoded.IndexOf(':');
            if (i < 0) return ("", "");
            return (decoded[..i], decoded[(i + 1)..]);
        }
        catch (FormatException)
        {
            return ("", "");
        }
    }

    private static bool VerifyHash(string pass, string hash)
    {
        // bcrypt ($2a$ / $2b$ / $2y$)。argon2 は未対応 (PHP 側でしか作れないので常に不一致)
        if (!hash.StartsWith("$2", StringComparison.Ordinal)) return false;
        try
        {
            return BCrypt.Net.BCrypt.Verify(pass, hash);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>平文比較でもタイミング差を出さない (hash_equals 相当)</summary>
    private static bool FixedEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    /// <summary>設定 UI でパスワードを保存するときに使う。PHP の password_verify でも照合できる</summary>
    public static string HashPassword(string plain) => BCrypt.Net.BCrypt.HashPassword(plain, workFactor: 10);

    public static string RealmHeader(AuthConfig auth)
    {
        var realm = string.IsNullOrEmpty(auth.Realm) ? "Library" : auth.Realm.Replace("\"", "");
        return $"Basic realm=\"{realm}\"";
    }
}
