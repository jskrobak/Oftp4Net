using Havit.Blazor.Components.Web;
using Havit.Blazor.Components.Web.Bootstrap;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.Core.Protocol;
using Oftp4Net.Domain;
using Oftp4Net.Services;

namespace Oftp4Net.Server.Components.Pages;

public partial class SendQueue : ComponentBase, IDisposable
{
    [Inject] protected IDataService DataService { get; set; } = null!;
    [Inject] protected IFileService FileService { get; set; } = null!;
    [Inject] protected IHxMessengerService Messenger { get; set; } = null!;
    [Inject] protected IHxMessageBoxService MessageBox { get; set; } = null!;
    [Inject] protected SendService SendService { get; set; } = null!;
    [Inject] protected OutboxStorage OutboxStorage { get; set; } = null!;
    [Inject] protected IConfiguration Configuration { get; set; } = null!;

    /// <summary>Virtual file formats of SFIDFMT as they are offered in the form.</summary>
    internal sealed record FileFormatOption(string Code, string Text);

    private static readonly FileFormatOption[] FileFormatOptions =
    [
        new(FileFormats.Unstructured, "U - unstructured"),
        new(FileFormats.Text, "T - text"),
        new(FileFormats.Fixed, "F - fixed records"),
        new(FileFormats.Variable, "V - variable records"),
    ];

    private void HandleFormatChanged()
    {
        if (!FileFormats.IsRecordStructured(currentSendQueueItem.Format))
            currentSendQueueItem.MaxRecordSize = 0;
    }

    private HxInputFile inputFileComponent = null!;
    private float? uploadProgress;
    private string? originalFilePath;
    private bool saved;
    private string? uploadedFilePath;

    private long MaxUploadFileSize => Configuration.GetValue("Upload:MaxOutboxFileSizeMB", 512L) * 1024 * 1024;
    
    private SendQueueItem currentSendQueueItem = new();
    private SendQueueFilter filterModel = new();
    private HxGrid<SendQueueItem> gridComponent = null!;
    private HxModal sendQueueItemEditModal = null!;
    private List<Partner> availablePartners = [];
    private List<Identity> availableIdentities = [];
    private HxButton btnAdd = null!;
    private int _seconds = 0;
    private int _progress = 0;
    private CancellationTokenSource _cts = new();


    protected override async Task OnInitializedAsync()
    {
        availablePartners = await DataService.GetAllPartnersAsync();
        availableIdentities = await DataService.GetAllIdentitiesAsync();

        await StartAutoRefreshAsync();
    }

    private async Task<GridDataProviderResult<SendQueueItem>> GetGridData(GridDataProviderRequest<SendQueueItem> request)
    {
        var response = await DataService.GetSendQueueItemsDataFragmentAsync(filterModel, request, request.CancellationToken);
        return new GridDataProviderResult<SendQueueItem>()
        {
            Data = response.Data,
            TotalCount = response.TotalCount
        };
    }
    
    private async Task HandleDeleteClick(SendQueueItem sendQueueItem)
    {
        await DataService.DeleteSendQueueItemAsync(sendQueueItem);
        await OutboxStorage.DeleteIfInOutboxAsync(sendQueueItem.FilePath);
        await gridComponent.RefreshDataAsync();
    }

    private async Task HandleNewItemClicked()
    {
        StopAutoRefresh();
        
        if (availablePartners.Count == 0)
        {
            Messenger.AddError("There is no Partner available. First add at least one Partner.");
            return;
        }
        
        if (availableIdentities.Count == 0)
        {
            Messenger.AddError("There is no Identity available. First add at least one Identity.");
            return;
        }

        currentSendQueueItem = new SendQueueItem()
        {
            PartnerId = availablePartners.First().Id,
            IdentityId = availableIdentities.First().Id
        };
        await ShowEditModalAsync();
    }
    
    private Task StartAutoRefreshAsync()
    {
        StopAutoRefresh();
        _cts = new CancellationTokenSource();
        _ = RunAutoRefreshAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task RunAutoRefreshAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                _seconds++;
                _progress = _seconds * 10;

                if (_seconds == 10)
                {
                    _seconds = 0;
                    await InvokeAsync(gridComponent.RefreshDataAsync);
                }

                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
            // Auto refresh stopped.
        }
    }
    
    private void StopAutoRefresh()
    {
        _cts.Cancel();
        _seconds = 0;
        _progress = 0;
    }

    public void Dispose()
    {
        _cts.Cancel();
    }

    private async Task HandleSelectedDataItemChanged()
    {
        // Clicking a selected row deselects it and sets the item to null.
        if (currentSendQueueItem is null)
            return;

        StopAutoRefresh();
        await ShowEditModalAsync();
    }

    private Partner? SelectedPartner => availablePartners.FirstOrDefault(p => p.Id == currentSendQueueItem.PartnerId);

    private async Task SaveSendQueueItem()
    {
        if (!FileService.FileExists(currentSendQueueItem.FilePath))
        {
            Messenger.AddError("File does not exist. Check the path.");
            return;
        }
        
        await DataService.SaveSendQueueItemAsync(currentSendQueueItem);
        SendService.Trigger();
        saved = true;

        // A replaced uploaded file is no longer needed.
        if (originalFilePath is not null && originalFilePath != currentSendQueueItem.FilePath)
            await OutboxStorage.DeleteIfInOutboxAsync(originalFilePath);
        
        await gridComponent.RefreshDataAsync();
        await sendQueueItemEditModal.HideAsync();
    }   

    private async Task HandleEditClick(SendQueueItem sendQueueItem)
    {
        StopAutoRefresh();
        
        currentSendQueueItem = sendQueueItem;
        
        await ShowEditModalAsync();
    }

    private async Task ShowEditModalAsync()
    {
        originalFilePath = currentSendQueueItem.Id == 0 ? null : currentSendQueueItem.FilePath;
        uploadProgress = null;
        saved = false;
        uploadedFilePath = null;
        await sendQueueItemEditModal.ShowAsync();
    }

    private async Task EditModalClosed()
    {
        // Remove a file uploaded in a dialog that was closed without saving.
        if (!saved)
            await OutboxStorage.DeleteIfInOutboxAsync(uploadedFilePath);

        await StartAutoRefreshAsync();
    }

    private async Task HandleClearQueue()
    {
        if (!await MessageBox.ConfirmAsync("Clear all messages in send queue?",
                "Are you sure you want to clear all messages from send queue?"))
            return;
        
        StopAutoRefresh();
        
        var items = await DataService.GetAllSendQueueItemsAsync();
        await DataService.DeleteAllSendQueueItemsAsync();
        foreach (var item in items)
            await OutboxStorage.DeleteIfInOutboxAsync(item.FilePath);
        
        await gridComponent.RefreshDataAsync();
        await StartAutoRefreshAsync();
    }

    private void HandleSendNowClicked()
    {
        SendService.Trigger();
        Messenger.AddInformation("Send queue processing started.");
    }

    private async Task HandleRequeueClick(SendQueueItem sendQueueItem)
    {
        await DataService.RequeueSendQueueItemAsync(sendQueueItem);
        SendService.Trigger();
        await gridComponent.RefreshDataAsync();
    }

    private static ThemeColor GetStatusColor(SendStatus status) => status switch
    {
        SendStatus.DELIVERED => ThemeColor.Success,
        SendStatus.SENT => ThemeColor.Info,
        SendStatus.ERROR => ThemeColor.Warning,
        SendStatus.FAILED or SendStatus.NOT_DELIVERED => ThemeColor.Danger,
        _ => ThemeColor.Secondary
    };

    private async Task HandleFileSelected(InputFileChangeEventArgs args)
    {
        if (args.FileCount == 0)
            return;

        uploadProgress = 0;
        await inputFileComponent.StartUploadAsync();
    }

    private void HandleUploadProgress(UploadProgressEventArgs args)
    {
        uploadProgress = args.UploadedBytes * 100f / Math.Max(1, args.UploadSize);
    }

    private async Task HandleFileUploaded(FileUploadedEventArgs args)
    {
        uploadProgress = null;

        if (args.ResponseStatus != System.Net.HttpStatusCode.OK)
        {
            Messenger.AddError(args.ResponseStatus == System.Net.HttpStatusCode.RequestEntityTooLarge
                ? $"The file is larger than {MaxUploadFileSize / 1024 / 1024} MB."
                : $"Upload failed ({(int)args.ResponseStatus}): {args.ResponseText}");
            return;
        }

        // Uploading again within the same dialog replaces the previous upload.
        await OutboxStorage.DeleteIfInOutboxAsync(uploadedFilePath);
        uploadedFilePath = System.Text.Json.JsonSerializer.Deserialize<string>(args.ResponseText)!;
        currentSendQueueItem.FilePath = uploadedFilePath;
        if (string.IsNullOrWhiteSpace(currentSendQueueItem.VirtualFileName))
            currentSendQueueItem.VirtualFileName = OutboxStorage.SuggestVirtualFileName(args.OriginalFileName);
    }
}
