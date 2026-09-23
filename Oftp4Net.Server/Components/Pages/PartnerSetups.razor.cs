using Havit.Blazor.Components.Web;
using Havit.Blazor.Components.Web.Bootstrap;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services;
using Oftp4Net.Services.Pdx;

namespace Oftp4Net.Server.Components.Pages;

public partial class PartnerSetups : ComponentBase
{
    private const int ShownDocuments = 200;

    [Inject] protected IPartnerSetupDocumentRepository Documents { get; set; } = null!;
    [Inject] protected PartnerSetupService SetupService { get; set; } = null!;
    [Inject] protected SendService SendService { get; set; } = null!;
    [Inject] protected ListenerService ListenerService { get; set; } = null!;
    [Inject] protected IHxMessengerService Messenger { get; set; } = null!;
    [CascadingParameter] private Task<AuthenticationState>? AuthenticationState { get; set; }

    private List<PartnerSetupDocument>? documents;
    private readonly HashSet<int> expanded = [];
    private PartnerSetupDocument? rejecting;
    private string? rejectReason;

    protected override Task OnInitializedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        documents = await Documents.GetLatestAsync(ShownDocuments);
    }

    private void Toggle(int id)
    {
        if (!expanded.Remove(id))
            expanded.Add(id);
    }

    private async Task<string?> GetUserAsync() =>
        AuthenticationState is null ? null : (await AuthenticationState).User.Identity?.Name;

    private async Task ApproveAsync(PartnerSetupDocument document)
    {
        try
        {
            await SetupService.ApproveAsync(document.Id, await GetUserAsync());
            // The EERP goes in the next session with the partner, the datasheet is applied after it.
            SendService.Trigger();
            Messenger.AddInformation($"The datasheet of {document.Partner?.Name ?? document.StationName} is approved; it is applied after the EERP has been sent.");
        }
        catch (Exception ex)
        {
            Messenger.AddError($"Approving failed: {ex.Message}");
        }

        await LoadAsync();
    }

    private async Task ApplyNowAsync(PartnerSetupDocument document)
    {
        try
        {
            await SetupService.ApplyNowAsync(document.Id, await GetUserAsync());
            Messenger.AddInformation($"The datasheet of {document.Partner?.Name ?? document.StationName} is applied.");
        }
        catch (Exception ex)
        {
            Messenger.AddError($"Applying failed: {ex.Message}");
        }

        await LoadAsync();
    }

    /// <summary>The stored changes, one per line: "Field: old -> new" or "Field: new".</summary>
    private static IEnumerable<(string Field, string? Old, string New)> ParseChanges(string changes)
    {
        foreach (var line in changes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0)
            {
                yield return (line, null, "");
                continue;
            }

            var value = line[(colon + 2)..];
            var arrow = value.IndexOf(" -> ", StringComparison.Ordinal);
            yield return arrow < 0
                ? (line[..colon], null, value)
                : (line[..colon], value[..arrow], value[(arrow + 4)..]);
        }
    }

    private void StartReject(PartnerSetupDocument document)
    {
        rejecting = document;
        rejectReason = "The OFTP2 Communication Setup was refused by the administrator.";
    }

    private async Task RejectAsync()
    {
        if (rejecting is null)
            return;

        try
        {
            await SetupService.RejectAsync(rejecting.Id, await GetUserAsync(), rejectReason ?? "");
            SendService.Trigger();
            Messenger.AddInformation($"The datasheet of {rejecting.Partner?.Name ?? rejecting.StationName} is rejected; the partner gets a NERP.");
        }
        catch (Exception ex)
        {
            Messenger.AddError($"Rejecting failed: {ex.Message}");
        }

        rejecting = null;
        await LoadAsync();
    }

    private static string SignatureText(PartnerSetupSignature signature) => signature switch
    {
        PartnerSetupSignature.Valid => "signed",
        PartnerSetupSignature.Invalid => "signature not verified",
        _ => "not signed",
    };

    private static string StatusText(PartnerSetupStatus status) => status switch
    {
        PartnerSetupStatus.PendingApproval => "waits for approval",
        PartnerSetupStatus.AwaitingEndResponse => "applied after EERP",
        PartnerSetupStatus.Scheduled => "scheduled",
        PartnerSetupStatus.Applied => "applied",
        PartnerSetupStatus.Rejected => "rejected",
        _ => "failed",
    };

    private static ThemeColor StatusColor(PartnerSetupStatus status) => status switch
    {
        PartnerSetupStatus.PendingApproval => ThemeColor.Warning,
        PartnerSetupStatus.AwaitingEndResponse or PartnerSetupStatus.Scheduled => ThemeColor.Info,
        PartnerSetupStatus.Applied => ThemeColor.Success,
        _ => ThemeColor.Danger,
    };
}
