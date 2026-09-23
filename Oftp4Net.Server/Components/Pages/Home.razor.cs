using Havit.Blazor.Components.Web.Bootstrap;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Oftp4Net.Services;
using Oftp4Net.Services.Health;

namespace Oftp4Net.Server.Components.Pages;

public partial class Home : ComponentBase, IDisposable
{
    [Inject] protected SendService SendService { get; set; } = null!;
    [Inject] protected ListenerService ListenerService { get; set; } = null!;
    [Inject] protected HealthMonitor HealthMonitor { get; set; } = null!;
    [Inject] protected HealthCheckService HealthCheckService { get; set; } = null!;

    protected override void OnInitialized()
    {
        ListenerService.StatusChanged += HandleStatusChanged;
        HealthMonitor.Updated += HandleStatusChanged;
    }

    private void HandleStatusChanged() => InvokeAsync(StateHasChanged);

    private async Task ToggleSendService()
    {
        if (SendService.IsPaused)
            SendService.Resume();
        else
            SendService.Pause();

        // The send service is degraded while it is paused; show it right away.
        await CheckHealthAsync();
    }

    private Task CheckHealthAsync() => HealthMonitor.CheckNowAsync(HealthCheckService);

    private static ThemeColor StatusColor(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => ThemeColor.Success,
        HealthStatus.Degraded => ThemeColor.Warning,
        _ => ThemeColor.Danger,
    };

    public void Dispose()
    {
        ListenerService.StatusChanged -= HandleStatusChanged;
        HealthMonitor.Updated -= HandleStatusChanged;
    }
}
