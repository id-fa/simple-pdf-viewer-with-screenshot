using System.Text;
using LibraryServer.Core.Auth;
using LibraryServer.Core.Config;
using Xunit;

namespace LibraryServer.Tests;

public class BasicAuthTests
{
    private static string Header(string user, string pass) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + pass));

    [Fact]
    public void Disabled_when_user_or_pass_empty()
    {
        Assert.False(BasicAuth.IsEnabled(null));
        Assert.False(BasicAuth.IsEnabled(new AuthConfig { User = "a" }));
        Assert.False(BasicAuth.IsEnabled(new AuthConfig { Pass = "a" }));
        Assert.True(BasicAuth.IsEnabled(new AuthConfig { User = "a", Pass = "b" }));
    }

    [Fact]
    public void Plaintext_password()
    {
        var auth = new AuthConfig { User = "reader", Pass = "s3cret" };
        Assert.True(BasicAuth.Verify(auth, Header("reader", "s3cret")));
        Assert.False(BasicAuth.Verify(auth, Header("reader", "wrong")));
        Assert.False(BasicAuth.Verify(auth, Header("other", "s3cret")));
        Assert.False(BasicAuth.Verify(auth, null));
        Assert.False(BasicAuth.Verify(auth, "Bearer xyz"));
        Assert.False(BasicAuth.Verify(auth, "Basic %%%"));
    }

    [Fact]
    public void Password_with_colon_is_split_at_first_colon()
    {
        var auth = new AuthConfig { User = "u", Pass = "p:w:d" };
        Assert.True(BasicAuth.Verify(auth, Header("u", "p:w:d")));
    }

    [Fact]
    public void Php_password_hash_bcrypt_2y_is_accepted()
    {
        // PHP 8.5: php -r "echo password_hash('s3cret', PASSWORD_BCRYPT);"
        const string phpHash = "$2y$12$1GHwhEu9MCpR8RXiJIPgg.hed7tIZbx6bzx.bjF.MNMChI2mlDePe";
        Assert.True(BasicAuth.Verify(new AuthConfig { User = "u", Pass = phpHash }, Header("u", "s3cret")));
        Assert.False(BasicAuth.Verify(new AuthConfig { User = "u", Pass = phpHash }, Header("u", "nope")));
        Assert.False(BasicAuth.Verify(new AuthConfig { User = "u", Pass = "$2y$12$broken" }, Header("u", "s3cret")));

        var ours = BasicAuth.HashPassword("s3cret");
        Assert.StartsWith("$2", ours);
        Assert.True(BasicAuth.Verify(new AuthConfig { User = "u", Pass = ours }, Header("u", "s3cret")));
        Assert.False(BasicAuth.Verify(new AuthConfig { User = "u", Pass = ours }, Header("u", "nope")));
    }

    [Fact]
    public void Unknown_hash_scheme_never_matches()
    {
        var auth = new AuthConfig { User = "u", Pass = "$argon2id$v=19$m=65536,t=4,p=1$abc$def" };
        Assert.False(BasicAuth.Verify(auth, Header("u", "$argon2id$v=19$m=65536,t=4,p=1$abc$def")));
    }

    [Fact]
    public void Realm_header_strips_quotes()
    {
        Assert.Equal("Basic realm=\"My Lib\"", BasicAuth.RealmHeader(new AuthConfig { Realm = "My \"Lib\"" }));
        Assert.Equal("Basic realm=\"Library\"", BasicAuth.RealmHeader(new AuthConfig { Realm = "" }));
    }
}
