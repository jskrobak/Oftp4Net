namespace Oftp4Net.Server;

public static class AuthClaims
{
    /// <summary>Present while the signed in user has to change the password (e.g. the default admin account).</summary>
    public const string MustChangePassword = "oftp4net:must_change_password";
}
