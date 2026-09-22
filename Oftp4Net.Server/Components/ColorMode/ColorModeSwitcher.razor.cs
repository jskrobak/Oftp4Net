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

    /// <summary>CSS class of the button, so it can match the surrounding items.</summary>
    [Parameter] public string? CssClass { get; set; }

    /// <summary>Render the name of the mode next to the icon (hidden in a collapsed sidebar).</summary>
    [Parameter] public bool ShowText { get; set; } = true;

    protected string Tooltip = GetTooltip(DefaultMode);
    protected string Text = GetText(DefaultMode);
    // Set before the first render: HxIcon does not accept null.
    protected IconBase Icon = GetIcon(DefaultMode);
    protected ColorMode ColorMode = ColorMode.Auto;

    protected override async Task OnInitializedAsync()
    {
        ColorMode = await GetCurrentModeAsync();
        
        Tooltip = GetTooltip(ColorMode);
        Icon = GetIcon(ColorMode);
        Text = GetText(ColorMode);
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
        Text = GetText(mode);

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

    private static string GetText(ColorMode mode) => mode switch
    {
        ColorMode.Auto => "Auto theme",
        ColorMode.Light => "Light theme",
        ColorMode.Dark => "Dark theme",
        _ => "Theme"
    };

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