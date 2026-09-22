using BitzArt.Blazor.Cookies;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Oftp4Net.Server.Components.Layout;

public partial class Sidebar : ComponentBase
{
    /// <summary>Id of the hidden form used to sign out (POST with an antiforgery token).</summary>
    private const string LogoutFormId = "oftp4net-logout-form";

    /// <summary>Items with this class open in a new browser tab.</summary>
    private const string ExternalLinkCssClass = "sidebar-external-link";

    [Inject] protected ICookieService CookieService { get; set; } = null!;
    [Inject] protected IJSRuntime JSRuntime { get; set; } = null!;

    private IJSObjectReference? _jsModule;

    private bool isCollapsed;

    protected override async Task OnInitializedAsync()
    {
        isCollapsed = await UiPreferences.ReadAsync(CookieService, UiPreferences.SidebarCollapsedCookie) == "true";
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
            await (await GetJsModuleAsync()).InvokeVoidAsync("openMarkedLinksInNewTab", ExternalLinkCssClass);
    }

    private async Task SignOutAsync()
    {
        await (await GetJsModuleAsync()).InvokeVoidAsync("submitForm", LogoutFormId);
    }

    private async Task<IJSObjectReference> GetJsModuleAsync() =>
        _jsModule ??= await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Components/Layout/Sidebar.razor.js");

    private async Task HandleCollapsedChanged()
    {
        isCollapsed = !isCollapsed;
        await UiPreferences.SaveAsync(CookieService, UiPreferences.SidebarCollapsedCookie,
            isCollapsed ? "true" : "false");
    }
}
