using Havit.Blazor.Components.Web.Bootstrap;
using Microsoft.AspNetCore.Components;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.Domain;
using Oftp4Net.Services;

namespace Oftp4Net.Server.Components.Pages;

public partial class ReceivedFiles : ComponentBase
{
    [Inject] protected IDataService DataService { get; set; } = null!;

    private ReceivedFileFilter filterModel = new();
    private HxGrid<ReceivedFile> gridComponent = null!;

    private async Task<GridDataProviderResult<ReceivedFile>> GetGridData(GridDataProviderRequest<ReceivedFile> request)
    {
        var response = await DataService.GetReceivedFilesDataFragmentAsync(filterModel, request, request.CancellationToken);
        return new GridDataProviderResult<ReceivedFile>
        {
            Data = response.Data,
            TotalCount = response.TotalCount
        };
    }

    private static ThemeColor GetStatusColor(ReceiveStatus status) => status switch
    {
        ReceiveStatus.CONFIRMED => ThemeColor.Success,
        ReceiveStatus.RECEIVED => ThemeColor.Info,
        ReceiveStatus.FAILED => ThemeColor.Danger,
        _ => ThemeColor.Secondary
    };
}
