using Havit.Blazor.Components.Web;
using Havit.Blazor.Components.Web.Bootstrap;
using Microsoft.AspNetCore.Components;
using Oftp4Net.Core.Protocol;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.Domain;
using Oftp4Net.Services;

namespace Oftp4Net.Server.Components.Pages;

public partial class ReceivedFiles : ComponentBase
{
    [Inject] protected IDataService DataService { get; set; } = null!;

    [Inject] protected IHxMessengerService Messenger { get; set; } = null!;

    private ReceivedFileFilter filterModel = new();
    private HxGrid<ReceivedFile> gridComponent = null!;

    private HxModal notDeliveredModal = null!;
    private ReceivedFile? notDeliveredFile;
    private string notDeliveredReasonCode = AnswerReasonCodes.UnspecifiedReason;
    private string? notDeliveredReasonText;

    /// <summary>Answer reason codes that make sense for a file that was received but not delivered further.</summary>
    private static readonly Dictionary<string, string> NerpReasons = new()
    {
        [AnswerReasonCodes.InvalidFilename] = "Invalid filename",
        [AnswerReasonCodes.InvalidDestination] = "Invalid destination",
        [AnswerReasonCodes.InvalidOrigin] = "Invalid origin",
        [AnswerReasonCodes.StorageRecordFormatNotSupported] = "Storage record format not supported",
        [AnswerReasonCodes.AccessMethodFailure] = "Access method failure",
        [AnswerReasonCodes.UnspecifiedReason] = "Unspecified reason",
    };

    private async Task<GridDataProviderResult<ReceivedFile>> GetGridData(GridDataProviderRequest<ReceivedFile> request)
    {
        var response = await DataService.GetReceivedFilesDataFragmentAsync(filterModel, request, request.CancellationToken);
        return new GridDataProviderResult<ReceivedFile>
        {
            Data = response.Data,
            TotalCount = response.TotalCount
        };
    }

    private async Task ShowNotDelivered(ReceivedFile file)
    {
        notDeliveredFile = file;
        notDeliveredReasonCode = AnswerReasonCodes.UnspecifiedReason;
        notDeliveredReasonText = null;
        await notDeliveredModal.ShowAsync();
    }

    private async Task ReportNotDelivered()
    {
        if (notDeliveredFile is null)
            return;

        try
        {
            await DataService.ReportReceivedFileNotDeliveredAsync(notDeliveredFile, notDeliveredReasonCode, notDeliveredReasonText);
            Messenger.AddInformation($"{notDeliveredFile.VirtualFileName} will be reported to the partner with a NERP.");
        }
        catch (Exception ex)
        {
            Messenger.AddError(ex.Message);
        }

        await notDeliveredModal.HideAsync();
        notDeliveredFile = null;
        await gridComponent.RefreshDataAsync();
    }

    private static ThemeColor GetStatusColor(ReceiveStatus status) => status switch
    {
        ReceiveStatus.CONFIRMED => ThemeColor.Success,
        ReceiveStatus.RECEIVED => ThemeColor.Info,
        ReceiveStatus.FAILED => ThemeColor.Danger,
        ReceiveStatus.NOT_DELIVERED => ThemeColor.Warning,
        ReceiveStatus.INTERRUPTED => ThemeColor.Warning,
        ReceiveStatus.HELD => ThemeColor.Warning,
        _ => ThemeColor.Secondary
    };
}
