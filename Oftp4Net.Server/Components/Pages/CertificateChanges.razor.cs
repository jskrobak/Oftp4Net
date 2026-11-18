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
    [Inject] protected GlobalSettingsService SettingsService { get; set; } = null!;
    [CascadingParameter] private Task<AuthenticationState> AuthenticationState { get; set; } = null!;

    /// <summary>Opens the form for the partner right away (from the menu of the partner).</summary>
    [SupplyParameterFromQuery(Name = "partner")] public int? PartnerQuery { get; set; }

    private static readonly PartnerCertificateUsage[] usages = Enum.GetValues<PartnerCertificateUsage>();
    private static readonly bool[] sources = [true, false];
    private static readonly bool[] targets = [false, true];

    private List<CertificateChange> changes = [];
    private HxModal scheduleModal = null!;
    private HxInputFile? inputFileComponent;
    private List<Partner> partners = [];
    private List<Certificate> certificates = [];
    private List<Certificate> ownCertificates = [];

    private int? partnerId;
    private PartnerCertificateUsage usage = PartnerCertificateUsage.Security;
    private DateTime activateAt;
    // Typed as text: the value of the browser's date and time input did not always reach the server.
    private string? activateAtText;
    private string? keepPreviousUntilText;
    private bool uploadFile = true;
    private int? certificateId;
    private string? note;
    private bool own;
    private int? replacedCertificateId;
    private DateTime? keepPreviousUntil;

    protected override async Task OnInitializedAsync()
    {
        partners = (await DataService.GetAllPartnersAsync()).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        certificates = await PartnerCertificatesAsync();
        ownCertificates = await OwnCertificatesAsync();
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

    private async Task<List<Certificate>> OwnCertificatesAsync() =>
        (await DataService.GetAllCertificatesAsync()).Where(c => c.HasPrivateKey).OrderBy(c => c.Name).ToList();

    private async Task ShowScheduleAsync(int? partner)
    {
        own = false;
        keepPreviousUntil = null;
        keepPreviousUntilText = null;
        // Our certificate in use is the one most likely replaced.
        var settings = await SettingsService.GetGlobalSettingsAsync();
        replacedCertificateId = settings.FileSecurityCertificateId ?? settings.OftpClientCertificateId;
        partnerId = partner;
        usage = PartnerCertificateUsage.Security;
        // A whole hour, a week ahead: a usual announcement of a partner.
        var now = TimeService.GetCurrentTime();
        activateAt = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddDays(7);
        activateAtText = LocalTimeText.ToText(activateAt);
        certificateId = null;
        note = null;
        uploadFile = true;
        await scheduleModal.ShowAsync();
    }

    /// <summary>An Odette certificate of ours usually serves file security and TLS alike.</summary>
    private void HandleTargetChanged() =>
        usage = own ? PartnerCertificateUsage.SecurityAndTls : PartnerCertificateUsage.Security;

    /// <summary>How the typed time is read, shown under the field so that a mistake is seen before it is saved.</summary>
    private string Interpret(string? text, bool required)
    {
        if (string.IsNullOrWhiteSpace(text))
            return required ? "Enter the time, e.g. 2026-12-03 10:00." : "";
        if (!LocalTimeText.TryParse(text, out var time))
            return "Not a time; write it as 2026-12-03 10:00.";
        var days = (time - TimeService.GetCurrentTime()).TotalDays;
        return $"{time:dddd d MMMM yyyy, HH:mm}" + (days < 0 ? " – in the past, applied right away." : $" – in {Math.Ceiling(days):0} day(s).");
    }

    private async Task HandleScheduleAsync()
    {
        if (!LocalTimeText.TryParse(activateAtText, out activateAt))
        {
            Messenger.AddWarning("The time it is used from is not a time; write it as 2026-12-03 10:00.");
            return;
        }

        if (string.IsNullOrWhiteSpace(keepPreviousUntilText))
            keepPreviousUntil = null;
        else if (LocalTimeText.TryParse(keepPreviousUntilText, out var until))
            keepPreviousUntil = until;
        else
        {
            Messenger.AddWarning("Until when the replaced certificate decrypts is not a time; write it as 2026-12-31 23:59.");
            return;
        }

        if (own && activateAt <= TimeService.GetCurrentTime() &&
            !await MessageBox.ConfirmAsync("Replace our certificate now", $"{activateAt:g} has passed: our certificate is replaced right away. Continue?"))
            return;

        if (own)
        {
            if (certificateId is null || replacedCertificateId is null)
            {
                Messenger.AddWarning("Select the new certificate and the one it replaces.");
                return;
            }

            await ScheduleAsync(() => ChangeService.ScheduleOwnAsync(certificateId.Value, replacedCertificateId.Value,
                usage, activateAt, keepPreviousUntil, note, UserName()));
            return;
        }

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
        if (!await MessageBox.ConfirmAsync("Cancel the change", $"Cancel the change of {CertificateChangeService.Whose(change)} at {change.ActivateAt:g}?"))
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
