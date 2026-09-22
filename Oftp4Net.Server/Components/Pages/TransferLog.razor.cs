using Havit.Blazor.Components.Web.Bootstrap;
using Microsoft.AspNetCore.Components;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services;

namespace Oftp4Net.Server.Components.Pages;

/// <summary>Transfer history of one category (outgoing, incoming, EERP/NERP, hooks).</summary>
public partial class TransferLog : ComponentBase
{
    /// <summary>URL segments of the sections in the Logs menu.</summary>
    public static readonly IReadOnlyDictionary<string, (TransferEventCategory Category, string Title)> Sections =
        new Dictionary<string, (TransferEventCategory, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["outgoing"] = (TransferEventCategory.Outgoing, "Outgoing"),
            ["incoming"] = (TransferEventCategory.Incoming, "Incoming"),
            ["eerp"] = (TransferEventCategory.EndResponse, "EERP / NERP"),
            ["hooks"] = (TransferEventCategory.Hook, "Hooks and webhooks"),
        };

    [Parameter] public string Section { get; set; } = "";

    [Inject] protected ITransferEventRepository Repository { get; set; } = null!;
    [Inject] protected GlobalSettingsService GlobalSettingsService { get; set; } = null!;
    [Inject] protected NavigationManager Navigation { get; set; } = null!;

    private static readonly TransferEventLevel[] levels = Enum.GetValues<TransferEventLevel>();

    private TransferEventFilter filterModel = new();
    private HxGrid<TransferEvent> gridComponent = null!;
    private HxModal detailModal = null!;
    private TransferEvent? selected;
    private int archiveDays = 90;
    private string? currentSection;

    private string Title => Sections.TryGetValue(Section, out var section) ? section.Title : "Transfer log";

    protected override async Task OnInitializedAsync()
    {
        archiveDays = (await GlobalSettingsService.GetGlobalSettingsAsync()).ArchiveEventsAfterDays;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!Sections.TryGetValue(Section, out var section))
        {
            Navigation.NavigateTo("Logs/outgoing");
            return;
        }

        // Switching between sections reuses the component instance: start with a fresh filter.
        if (currentSection is not null && !string.Equals(currentSection, Section, StringComparison.OrdinalIgnoreCase))
        {
            filterModel = new TransferEventFilter { Category = section.Category };
            if (gridComponent is not null)
                await gridComponent.RefreshDataAsync();
        }

        filterModel.Category = section.Category;
        currentSection = Section;
    }

    private async Task<GridDataProviderResult<TransferEvent>> GetGridData(GridDataProviderRequest<TransferEvent> request)
    {
        var response = await Repository.GetFragmentAsync(filterModel, request, request.CancellationToken);
        return new GridDataProviderResult<TransferEvent>
        {
            Data = response.Data,
            TotalCount = response.TotalCount
        };
    }

    private async Task HandleSelectedAsync(TransferEvent? transferEvent)
    {
        selected = transferEvent;
        if (selected is not null)
            await detailModal.ShowAsync();
    }

    private static ThemeColor GetColor(TransferEventLevel level) => level switch
    {
        TransferEventLevel.Error => ThemeColor.Danger,
        TransferEventLevel.Warning => ThemeColor.Warning,
        _ => ThemeColor.Success
    };
}
