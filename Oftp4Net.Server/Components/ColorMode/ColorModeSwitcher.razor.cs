using System.Drawing;
using BitzArt.Blazor.Cookies;
using Havit.Blazor.Components.Web;
using Havit.Blazor.Components.Web.Bootstrap;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Oftp4Net.Server.Components.ColorMode;

public partial class ColorModeSwitcher : ComponentBase
{
    const string cookieName = "oftp4net-color-mode";

    protected string Tooltip = "";
    protected IconBase? Icon;
    protected Havit.Blazor.Components.Web.Bootstrap.ColorMode ColorMode;

    protected override async Task OnInitializedAsync()
    {
        ColorMode = await GetCurrentModeAsync();
        
        Tooltip = GetTooltip(ColorMode);
        Icon = GetIcon(ColorMode);
        
    }

    [Inject] ICookieService CookieService { get; set; } = null!;
    [Inject] protected IJSRuntime JSRuntime { get; set; } = null!;

    private IJSObjectReference? _jsModule;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);
        //await ApplyModeAsync(await GetCurrentModeAsync());
    }

    private async Task<Havit.Blazor.Components.Web.Bootstrap.ColorMode> GetCurrentModeAsync()
    {
        var cookie = await CookieService.GetAsync(cookieName);

        if (cookie == null)
            return Havit.Blazor.Components.Web.Bootstrap.ColorMode.Auto;
		
        if(!int.TryParse(cookie.Value, out int mode))
            return Havit.Blazor.Components.Web.Bootstrap.ColorMode.Auto;
		
        return (Havit.Blazor.Components.Web.Bootstrap.ColorMode)mode;
    }
	
    private async Task ApplyModeAsync(Havit.Blazor.Components.Web.Bootstrap.ColorMode mode)
    {
        Tooltip = GetTooltip(mode);
        Icon = GetIcon(mode);
        
        await EnsureJsModule();
        await _jsModule!.InvokeVoidAsync("setColorMode", mode.ToString("g").ToLowerInvariant());
        
        await CookieService.SetAsync(cookieName, ((int)mode).ToString());
    }
    
    private async Task HandleClick()
    {
        var newColorMode = await GetCurrentModeAsync() switch
        {
            Havit.Blazor.Components.Web.Bootstrap.ColorMode.Auto => Havit.Blazor.Components.Web.Bootstrap.ColorMode.Dark,
            Havit.Blazor.Components.Web.Bootstrap.ColorMode.Dark => Havit.Blazor.Components.Web.Bootstrap.ColorMode.Light,
            Havit.Blazor.Components.Web.Bootstrap.ColorMode.Light => Havit.Blazor.Components.Web.Bootstrap.ColorMode.Auto,
            _ => Havit.Blazor.Components.Web.Bootstrap.ColorMode.Auto // fallback
        };

        await ApplyModeAsync(newColorMode);
    }
    
    private async Task EnsureJsModule()
    {
        _jsModule ??= await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Components/ColorMode/ColorModeSwitcher.razor.js");
    }

    private static IconBase GetIcon(Havit.Blazor.Components.Web.Bootstrap.ColorMode mode)
    {
        return mode switch
        {
            Havit.Blazor.Components.Web.Bootstrap.ColorMode.Auto => BootstrapIcon.CircleHalf,
            Havit.Blazor.Components.Web.Bootstrap.ColorMode.Light => BootstrapIcon.Sun,
            Havit.Blazor.Components.Web.Bootstrap.ColorMode.Dark => BootstrapIcon.Moon,
            _ => throw new InvalidOperationException($"Unknown color mode")
        };
    }

    private static string GetTooltip(Havit.Blazor.Components.Web.Bootstrap.ColorMode mode)
    {
        return mode switch
        {
            Havit.Blazor.Components.Web.Bootstrap.ColorMode.Auto => "Auto color mode (theme). Click to switch to Dark.",
            Havit.Blazor.Components.Web.Bootstrap.ColorMode.Dark => "Dark color mode (theme). Click to switch to Light.",
            Havit.Blazor.Components.Web.Bootstrap.ColorMode.Light => "Light color mode (theme). Click to switch to Auto.",
            _ => "Click to switch color mode (theme) to Auto." // fallback
        };
    }
}