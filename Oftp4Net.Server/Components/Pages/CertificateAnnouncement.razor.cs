using Havit.Blazor.Components.Web;
using Havit.Blazor.Components.Web.Bootstrap;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Oftp4Net.Services;
using Oftp4Net.Services.Mail;
using Oftp4Net.Services.Security;

namespace Oftp4Net.Server.Components.Pages;

public partial class CertificateAnnouncement : ComponentBase
{
    [Parameter] public int ChangeId { get; set; }

    [Inject] protected CertificateAnnouncementService Announcements { get; set; } = null!;
    [Inject] protected MailService MailService { get; set; } = null!;
    [Inject] protected GlobalSettingsService SettingsService { get; set; } = null!;
    [Inject] protected NavigationManager Navigation { get; set; } = null!;
    [Inject] protected IHxMessengerService Messenger { get; set; } = null!;
    [Inject] protected IHxMessageBoxService MessageBox { get; set; } = null!;
    [CascadingParameter] private Task<AuthenticationState> AuthenticationState { get; set; } = null!;

    private AnnouncementPlan? plan;
    private string? error;
    private string? mailProblem;
    private string? pageUrl;
    private bool viaOftp = true;
    private readonly HashSet<string> selected = [];
    private string? preview;
    private string? testAddress;
    private bool busy;
    private List<AnnouncementResult>? results;
    private string? subject;
    private string? body;

    protected override async Task OnInitializedAsync()
    {
        var settings = await SettingsService.GetGlobalSettingsAsync();
        // The page as partners reach it: the address the administration runs at.
        pageUrl = settings.PublishCertificates ? new Uri(new Uri(Navigation.BaseUri), "cert").ToString() : null;
        subject = string.IsNullOrWhiteSpace(settings.CertificateAnnouncementSubject)
            ? CertificateAnnouncementTemplate.DefaultSubject : settings.CertificateAnnouncementSubject;
        body = string.IsNullOrWhiteSpace(settings.CertificateAnnouncementBody)
            ? CertificateAnnouncementTemplate.DefaultBody : settings.CertificateAnnouncementBody;
        mailProblem = MailService.Problem;
        testAddress = (await AuthenticationState).User.Identity?.Name is { } name && name.Contains('@') ? name : null;
        await LoadAsync();
        // Messages sent already are not selected again by default.
        foreach (var mail in plan?.Mails.Where(m => m.SentAt is null) ?? [])
            selected.Add(mail.Key);
    }

    private async Task LoadAsync()
    {
        try
        {
            plan = await Announcements.PlanAsync(ChangeId, pageUrl, viaOftp);
            selected.IntersectWith(plan.Mails.Select(m => m.Key));
            error = null;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
        }
    }

    private void SelectAll(bool all)
    {
        selected.Clear();
        if (all && plan is not null)
            selected.UnionWith(plan.Mails.Select(m => m.Key));
    }

    private void Toggle(string key, bool on)
    {
        if (on)
            selected.Add(key);
        else
            selected.Remove(key);
    }

    private async Task SendTestAsync()
    {
        var key = plan?.Mails.FirstOrDefault(m => selected.Contains(m.Key))?.Key;
        if (key is null || string.IsNullOrWhiteSpace(testAddress))
        {
            Messenger.AddWarning("Select a message and enter the address for the test.");
            return;
        }

        busy = true;
        try
        {
            await Announcements.SendTestAsync(ChangeId, pageUrl, viaOftp, key, testAddress);
            Messenger.AddInformation($"Test sent to {testAddress}.");
        }
        catch (Exception ex) when (ex is MailException or InvalidOperationException)
        {
            Messenger.AddError(ex.Message);
        }
        finally
        {
            busy = false;
        }
    }

    private async Task SendAsync()
    {
        if (plan is null)
            return;
        var again = plan.Mails.Count(m => selected.Contains(m.Key) && m.SentAt is not null);
        if (!await MessageBox.ConfirmAsync("Send the announcement",
                $"Send {selected.Count} e-mail(s) to the partners{(viaOftp ? " and queue the certificate for delivery over OFTP" : "")}?" +
                (again > 0 ? $" {again} of them were sent already." : "")))
            return;

        busy = true;
        try
        {
            results = await Announcements.SendAsync(ChangeId, pageUrl, viaOftp, selected.ToList(),
                (await AuthenticationState).User.Identity?.Name);
            var failed = results.Count(r => r.Error is not null);
            if (failed == 0)
                Messenger.AddInformation($"{results.Count} e-mail(s) sent.");
            else
                Messenger.AddWarning($"{results.Count - failed} sent, {failed} failed.");
            selected.Clear();
            selected.UnionWith(results.Where(r => r.Error is not null).Select(r => r.Key));
            await LoadAsync();
        }
        finally
        {
            busy = false;
        }
    }

    private async Task SaveTemplateAsync()
    {
        var settings = await SettingsService.GetGlobalSettingsAsync();
        settings.CertificateAnnouncementSubject = subject == CertificateAnnouncementTemplate.DefaultSubject ? null : subject;
        settings.CertificateAnnouncementBody = Normalize(body) == Normalize(CertificateAnnouncementTemplate.DefaultBody) ? null : body;
        await SettingsService.SetGlobalSettingsAsync(settings);
        Messenger.AddInformation("Text saved.");
        await LoadAsync();
    }

    private void ResetTemplate()
    {
        subject = CertificateAnnouncementTemplate.DefaultSubject;
        body = CertificateAnnouncementTemplate.DefaultBody;
    }

    private static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Trim();
}
