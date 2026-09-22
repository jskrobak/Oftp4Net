using BitzArt.Blazor.Cookies;
using Microsoft.AspNetCore.Components;

namespace Oftp4Net.Server.Components.Layout;

public partial class Sidebar : ComponentBase
{
    [Inject] protected ICookieService CookieService { get; set; } = null!;

    private bool isCollapsed;

    protected override async Task OnInitializedAsync()
    {
        isCollapsed = await UiPreferences.ReadAsync(CookieService, UiPreferences.SidebarCollapsedCookie) == "true";
    }

    private async Task HandleCollapsedChanged()
    {
        isCollapsed = !isCollapsed;
        await UiPreferences.SaveAsync(CookieService, UiPreferences.SidebarCollapsedCookie,
            isCollapsed ? "true" : "false");
    }
}
