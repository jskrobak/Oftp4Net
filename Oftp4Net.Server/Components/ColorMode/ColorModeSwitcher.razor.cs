using BitzArt.Blazor.Cookies;
using Havit.Blazor.Components.Web;
using Havit.Blazor.Components.Web.Bootstrap;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Oftp4Net.Server.Components.ColorMode;

using ColorMode = Havit.Blazor.Components.Web.Bootstrap.ColorMode;

public partial class ColorModeSwitcher : ComponentBase
{
    private static readonly ColorMode DefaultMode = ColorMode.Auto;

    protected string Tooltip = "";
    protected IconBase? Icon;
    protected ColorMode ColorMode = ColorMode.Auto;

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

        // The page applies the stored mode already while loading (see App.razor); this covers the rest,
        // for example an enhanced navigation that did not run the script.
        if (firstRender)
            await ApplyToDocumentAsync(ColorMode);
    }

    private async Task<ColorMode> GetCurrentModeAsync()
    {
        var value = await UiPreferences.ReadAsync(CookieService, UiPreferences.ColorModeCookie);

        if (string.IsNullOrEmpty(value))
            return DefaultMode;

        // The mode is stored by name; older cookies hold the numeric value of the enumeration.
        if (Enum.TryParse<ColorMode>(value, ignoreCase: true, out var mode))
            return mode;

        return int.TryParse(value, out var number) && Enum.IsDefined(typeof(ColorMode), number)
            ? (ColorMode)number
            : DefaultMode;
    }
	
    private async Task ApplyModeAsync(ColorMode mode)
    {
        ColorMode = mode;
        Tooltip = GetTooltip(mode);
        Icon = GetIcon(mode);

        await ApplyToDocumentAsync(mode);
        await UiPreferences.SaveAsync(CookieService, UiPreferences.ColorModeCookie, mode.ToString("g").ToLowerInvariant());
    }

    private async Task ApplyToDocumentAsync(ColorMode mode)
    {
        await EnsureJsModule();
        await _jsModule!.InvokeVoidAsync("setColorMode", mode.ToString("g").ToLowerInvariant());
    }
    
    private async Task HandleClick()
    {
        var newColorMode = await GetCurrentModeAsync() switch
        {
            ColorMode.Auto => ColorMode.Dark,
            ColorMode.Dark => ColorMode.Light,
            ColorMode.Light => ColorMode.Auto,
            _ => ColorMode.Auto // fallback
        };

        await ApplyModeAsync(newColorMode);
    }
    
    private async Task EnsureJsModule()
    {
        _jsModule ??= await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Components/ColorMode/ColorModeSwitcher.razor.js");
    }

    private static IconBase GetIcon(ColorMode mode)
    {
        return mode switch
        {
            ColorMode.Auto => BootstrapIcon.CircleHalf,
            ColorMode.Light => BootstrapIcon.Sun,
            ColorMode.Dark => BootstrapIcon.Moon,
            _ => throw new InvalidOperationException($"Unknown color mode")
        };
    }

    private static string GetTooltip(ColorMode mode)
    {
        return mode switch
        {
            ColorMode.Auto => "Auto color mode (theme). Click to switch to Dark.",
            ColorMode.Dark => "Dark color mode (theme). Click to switch to Light.",
            ColorMode.Light => "Light color mode (theme). Click to switch to Auto.",
            _ => "Click to switch color mode (theme) to Auto." // fallback
        };
    }
}