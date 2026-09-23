using Havit.Blazor.Components.Web;
using Havit.Blazor.Components.Web.Bootstrap;
using Microsoft.AspNetCore.Components;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.Domain;
using Oftp4Net.Services;

namespace Oftp4Net.Server.Components.Pages;

public partial class Identities : ComponentBase
{
    [Inject] protected IDataService DataService { get; set; } = null!;
    [Inject] protected IHxMessengerService Messenger { get; set; } = null!;
    [Inject] protected IHxMessageBoxService MessageBox { get; set; } = null!;
    
    private Identity currentIdentity = new();
    private HashSet<Identity> selectedItems = [];
    private IdentityFilter filterModel = new();
    private HxGrid<Identity> gridComponent = null!;
    private HxModal identityEditModal = null!;
    
    
    private async Task<GridDataProviderResult<Identity>> GetGridData(GridDataProviderRequest<Identity> request)
    {
        var response = await DataService.GetIdentitiesDataFragmentAsync(filterModel, request, request.CancellationToken);
        return new GridDataProviderResult<Identity>()
        {
            Data = response.Data,
            TotalCount = response.TotalCount
        };
    }
    
    private async Task HandleDeleteClick(Identity identity)
    {
        await DataService.DeleteIdentityAsync(identity);
        await gridComponent.RefreshDataAsync();
    }
    
    private async Task HandleNewItemClicked()
    {
        currentIdentity = new Identity();
        await identityEditModal.ShowAsync();
    }

    private async Task HandleSelectedDataItemChanged()
    {
        // Clicking a selected row deselects it and sets the item to null.
        if (currentIdentity is null)
            return;

        await identityEditModal.ShowAsync();
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
                await DataService.DeleteIdentityAsync(item);
        }
        catch (Exception ex)
        {
            Messenger.AddError($"Delete failed: {ex.Message}");
        }

        selectedItems.Clear();
        await gridComponent.RefreshDataAsync();
    }

    private async Task SaveIdentity()
    {
        await DataService.SaveIdentityAsync(currentIdentity);
        
        await gridComponent.RefreshDataAsync();
        await identityEditModal.HideAsync();
    }   

    private HxModal exportModal = null!;
    private List<Identity>? exportIdentities;
    private int? exportIdentityId;

    private async Task HandleExportClick(Identity identity)
    {
        exportIdentities = await DataService.GetAllIdentitiesAsync();
        exportIdentityId = identity.Id;
        await exportModal.ShowAsync();
    }

    private async Task HandleEditClick(Identity identity)
    {
        currentIdentity = identity;
        await identityEditModal.ShowAsync();
    }
}