using System.Net;
using Havit.Blazor.Components.Web;
using Havit.Blazor.Components.Web.Bootstrap;
using Havit.Services.TimeServices;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services;
using Oftp4Net.Services.Security;

namespace Oftp4Net.Server.Components.Pages;

public partial class CertificateChanges : ComponentBase
{
    private const int HistoryCount = 200;

    [Inject] protected IDataService DataService { get; set; } = null!;
    [Inject] protected ICertificateChangeRepository ChangeRepository { get; set; } = null!;
    [Inject] protected CertificateChangeService ChangeService { get; set; } = null!;
    [Inject] protected IUploadService UploadService { get; set; } = null!;
    [Inject] protected IHxMessengerService Messenger { get; set; } = null!;
    [Inject] protected IHxMessageBoxService MessageBox { get; set; } = null!;
    [Inject] protected ITimeService TimeService { get; set; } = null!;
    [CascadingParameter] private Task<AuthenticationState> AuthenticationState { get; set; } = null!;

    /// <summary>Opens the form for the partner right away (from the menu of the partner).</summary>
    [SupplyParameterFromQuery(Name = "partner")] public int? PartnerQuery { get; set; }

    private static readonly PartnerCertificateUsage[] usages = Enum.GetValues<PartnerCertificateUsage>();
    private static readonly bool[] sources = [true, false];

    private List<CertificateChange> changes = [];
    private HxModal scheduleModal = null!;
    private HxInputFile? inputFileComponent;
    private List<Partner> partners = [];
    private List<Certificate> certificates = [];

    private int? partnerId;
    private PartnerCertificateUsage usage = PartnerCertificateUsage.Security;
    private DateTime activateAt;
    private bool uploadFile = true;
    private int? certificateId;
    private string? note;

    protected override async Task OnInitializedAsync()
    {
        partners = (await DataService.GetAllPartnersAsync()).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        certificates = await PartnerCertificatesAsync();
        await LoadAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && PartnerQuery is { } partner && partners.Any(p => p.Id == partner))
            await ShowScheduleAsync(partner);
    }

    private async Task LoadAsync() => changes = await ChangeRepository.GetOverviewAsync(HistoryCount);

    private async Task<List<Certificate>> PartnerCertificatesAsync() =>
        (await DataService.GetAllCertificatesAsync()).Where(c => !c.HasPrivateKey).OrderBy(c => c.Name).ToList();

    private async Task ShowScheduleAsync(int? partner)
    {
        partnerId = partner;
        usage = PartnerCertificateUsage.Security;
        // A whole hour, a week ahead: a usual announcement of a partner.
        var now = TimeService.GetCurrentTime();
        activateAt = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddDays(7);
        certificateId = null;
        note = null;
        uploadFile = true;
        await scheduleModal.ShowAsync();
    }

    private async Task HandleScheduleAsync()
    {
        if (partnerId is null)
        {
            Messenger.AddWarning("Select the partner.");
            return;
        }

        if (uploadFile)
        {
            // The file goes to the upload endpoint first; the schedule follows in HandleFileUploaded.
            await inputFileComponent!.StartUploadAsync();
            return;
        }

        if (certificateId is null)
        {
            Messenger.AddWarning("Select the certificate.");
            return;
        }

        await ScheduleAsync(() => ChangeService.ScheduleAsync(partnerId.Value, certificateId.Value, usage, activateAt, note, UserName()));
    }

    private async Task HandleFileUploaded(FileUploadedEventArgs uploaded)
    {
        if (uploaded is not { ResponseStatus: HttpStatusCode.OK })
        {
            Messenger.AddError($"Upload failed: HTTP {uploaded.ResponseStatus}.");
            return;
        }

        var data = await UploadService.ReadAllBytesAsync(uploaded.ResponseText.Replace("\"", ""));
        await ScheduleAsync(() => ChangeService.ScheduleFileAsync(partnerId!.Value, data, uploaded.OriginalFileName, usage, activateAt, note, UserName()));
    }

    private async Task ScheduleAsync(Func<Task<CertificateChange>> schedule)
    {
        try
        {
            var change = await schedule();
            Messenger.AddInformation(change.ActivateAt <= TimeService.GetCurrentTime()
                ? "The certificate is applied right away."
                : $"The certificate is used from {change.ActivateAt:g}.");
        }
        catch (InvalidOperationException ex)
        {
            Messenger.AddError(ex.Message);
            return;
        }

        await scheduleModal.HideAsync();
        certificates = await PartnerCertificatesAsync();
        await LoadAsync();
    }

    private async Task HandleCancelAsync(CertificateChange change)
    {
        if (!await MessageBox.ConfirmAsync("Cancel the change", $"Cancel the certificate change of {change.PartnerName} at {change.ActivateAt:g}?"))
            return;

        try
        {
            await ChangeService.CancelAsync(change.Id, UserName());
        }
        catch (InvalidOperationException ex)
        {
            Messenger.AddError(ex.Message);
        }

        await LoadAsync();
    }

    private string? UserName() => AuthenticationState.Result.User.Identity?.Name;

    private static ThemeColor StatusColor(CertificateChangeStatus status) => status switch
    {
        CertificateChangeStatus.Scheduled => ThemeColor.Info,
        CertificateChangeStatus.Applied => ThemeColor.Success,
        CertificateChangeStatus.Failed => ThemeColor.Danger,
        _ => ThemeColor.Secondary,
    };
}
