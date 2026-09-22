using System.Security.Authentication;
using Havit.Blazor.Components.Web;
using Havit.Blazor.Components.Web.Bootstrap;
using Microsoft.AspNetCore.Components;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.Domain;
using Oftp4Net.Services;

namespace Oftp4Net.Server.Components.Pages;

public partial class Listeners : ComponentBase, IDisposable
{
    [Inject] protected IDataService DataService { get; set; } = null!;
    [Inject] protected ListenerService ListenerService { get; set; } = null!;
    [Inject] protected IHxMessengerService Messenger { get; set; } = null!;

    private Listener currentListener = new();
    private ListenerFilter filterModel = new();
    private HxGrid<Listener> gridComponent = null!;
    private HxModal listenerEditModal = null!;
    private List<Identity> availableIdentities = [];
    private List<Certificate> availableCertificates = [];

    private List<int> SelectedTlsVersions
    {
        get => OftpDefaultsTls().Where(p => currentListener.Tls.HasFlag(p)).Select(p => (int)p).ToList();
        set => currentListener.Tls = value.Aggregate(SslProtocols.None, (current, item) => current | (SslProtocols)item);
    }

    private static IEnumerable<SslProtocols> OftpDefaultsTls() => Core.OftpDefaults.AllowedSslProtocols;

    protected override async Task OnInitializedAsync()
    {
        availableIdentities = await DataService.GetAllIdentitiesAsync();
        availableCertificates = await DataService.GetAllCertificatesAsync();
        ListenerService.StatusChanged += HandleStatusChanged;
    }

    private void HandleStatusChanged() => InvokeAsync(StateHasChanged);

    private ListenerStatus? GetStatus(Listener listener) =>
        ListenerService.Status.FirstOrDefault(s => s.ListenerId == listener.Id);

    private async Task<GridDataProviderResult<Listener>> GetGridData(GridDataProviderRequest<Listener> request)
    {
        var response = await DataService.GetListenersDataFragmentAsync(filterModel, request, request.CancellationToken);
        return new GridDataProviderResult<Listener>
        {
            Data = response.Data,
            TotalCount = response.TotalCount
        };
    }

    private async Task HandleNewItemClicked()
    {
        currentListener = new Listener
        {
            Name = "OFTP over TLS",
            IdentityId = availableIdentities.FirstOrDefault()?.Id,
        };
        await listenerEditModal.ShowAsync();
    }

    private async Task HandleSelectedDataItemChanged()
    {
        // Clicking a selected row deselects it and sets the item to null.
        if (currentListener is null)
            return;

        await listenerEditModal.ShowAsync();
    }

    private async Task HandleEditClick(Listener listener)
    {
        currentListener = listener;
        await listenerEditModal.ShowAsync();
    }

    private async Task HandleDeleteClick(Listener listener)
    {
        await DataService.DeleteListenerAsync(listener);
        await ReloadListenersAsync();
        await gridComponent.RefreshDataAsync();
    }

    private async Task SaveListener()
    {
        if (currentListener.UseTls && currentListener.CertificateId is null)
        {
            Messenger.AddError("TLS requires a server certificate.");
            return;
        }

        await DataService.SaveListenerAsync(currentListener);
        await listenerEditModal.HideAsync();
        await ReloadListenersAsync();
        await gridComponent.RefreshDataAsync();
    }

    private async Task HandleRestartClicked()
    {
        await ReloadListenersAsync();
        Messenger.AddInformation("Listeners restarted.");
    }

    private async Task ReloadListenersAsync()
    {
        try
        {
            await ListenerService.ReloadAsync();
        }
        catch (Exception ex)
        {
            Messenger.AddError($"Restarting listeners failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        ListenerService.StatusChanged -= HandleStatusChanged;
    }
}
