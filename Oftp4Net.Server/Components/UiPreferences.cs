using BitzArt.Blazor.Cookies;

namespace Oftp4Net.Server.Components;

/// <summary>
/// Preferences of the administration UI kept in cookies of the browser: colour mode and the collapsed sidebar.
/// The cookies are readable by scripts, so the colour mode can be applied before the page is rendered.
/// </summary>
public static class UiPreferences
{
    public const string ColorModeCookie = "oftp4net-color-mode";
    public const string SidebarCollapsedCookie = "oftp4net-sidebar-collapsed";

    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);

    public static async Task SaveAsync(ICookieService cookies, string key, string value) =>
        await cookies.SetAsync(key, value, DateTimeOffset.Now + Lifetime, httpOnly: false, secure: false);

    public static async Task<string?> ReadAsync(ICookieService cookies, string key) =>
        (await cookies.GetAsync(key))?.Value;
}
