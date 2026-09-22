using System.Security.Authentication;
using Havit.Blazor.Components.Web;
using Havit.Blazor.Components.Web.Bootstrap;
using Microsoft.AspNetCore.Components;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.Domain;
using Oftp4Net.Services;

namespace Oftp4Net.Server.Components.Pages;

public partial class Partners : ComponentBase
{
    [Inject] protected IDataService DataService { get; set; } = null!;
    [Inject] protected IHxMessengerService Messenger { get; set; } = null!;
    [Inject] protected IHxMessageBoxService MessageBox { get; set; } = null!;
    
    private Partner currentPartner = new();
    private HashSet<Partner> selectedItems = [];
    private PartnerFilter filterModel = new();
    private HxGrid<Partner> gridComponent = null!;
    private HxModal partnerEditModal = null!;
    
    private List<Certificate> availableCertificates = [];

    protected override async Task OnInitializedAsync()
    {
        availableCertificates = await DataService.GetAllCertificatesAsync();
    }
    
    private List<SslProtocols> GetTlsVersions()
    {
        return Enum.GetValues<SslProtocols>().ToList();
    }
                            
    private List<int> SelectedTlsVersions
    {
        get => GetTlsVersions().Where(p => p != SslProtocols.None && currentPartner.Tls.HasFlag(p)).Select(p => (int)p).ToList();
        set => currentPartner.Tls = value.Aggregate((SslProtocols)0, (current, item) => current | (SslProtocols)item);
    }
    
    private async Task<GridDataProviderResult<Partner>> GetGridData(GridDataProviderRequest<Partner> request)
    {
        var response = await DataService.GetPartnersDataFragmentAsync(filterModel, request, request.CancellationToken);
        return new GridDataProviderResult<Partner>()
        {
            Data = response.Data,
            TotalCount = response.TotalCount
        };
    }
    
    private async Task HandleDeleteClick(Partner partner)
    {
        await DataService.DeletePartnerAsync(partner);
        await gridComponent.RefreshDataAsync();
    }
    
    private async Task HandleNewItemClicked()
    {
        currentPartner = new Partner();
        await partnerEditModal.ShowAsync();
    }

    private async Task HandleSelectedDataItemChanged()
    {
        // Clicking a selected row deselects it and sets the item to null.
        if (currentPartner is null)
            return;

        await partnerEditModal.ShowAsync();
    }


    private async Task HandleDeleteSelected()
    {
        if (selectedItems.Count == 0)
        {
            Messenger.AddWarning("No item is selected.");
            return;
        }

        if (!await MessageBox.ConfirmAsync("Delete", $"Delete {selectedItems.Count} selected item(s)?"))
            return;

        try
        {
            foreach (var item in selectedItems.ToList())
                await DataService.DeletePartnerAsync(item);
        }
        catch (Exception ex)
        {
            Messenger.AddError($"Delete failed: {ex.Message}");
        }

        selectedItems.Clear();
        await gridComponent.RefreshDataAsync();
    }

    private async Task SavePartner()
    {
        await DataService.SavePartnerAsync(currentPartner);
        
        await gridComponent.RefreshDataAsync();
        await partnerEditModal.HideAsync();
    }   

    private async Task HandleEditClick(Partner partner)
    {
        currentPartner = partner;
        await partnerEditModal.ShowAsync();
    }
}