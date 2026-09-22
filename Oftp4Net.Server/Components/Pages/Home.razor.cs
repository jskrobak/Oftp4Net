using Microsoft.AspNetCore.Components;
using Oftp4Net.Services;

namespace Oftp4Net.Server.Components.Pages;

public partial class Home : ComponentBase, IDisposable
{
    [Inject] protected SendService SendService { get; set; } = null!;
    [Inject] protected ListenerService ListenerService { get; set; } = null!;

    protected override void OnInitialized()
    {
        ListenerService.StatusChanged += HandleStatusChanged;
    }

    private void HandleStatusChanged() => InvokeAsync(StateHasChanged);

    private void ToggleSendService()
    {
        if (SendService.IsPaused)
            SendService.Resume();
        else
            SendService.Pause();
    }

    public void Dispose()
    {
        ListenerService.StatusChanged -= HandleStatusChanged;
    }
}
