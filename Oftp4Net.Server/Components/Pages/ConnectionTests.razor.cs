using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Havit.Blazor.Components.Web;
using Havit.Blazor.Components.Web.Bootstrap;
using Havit.Services.TimeServices;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Oftp4Net.Core.Transport;
using Oftp4Net.Domain;
using Oftp4Net.Services;
using Oftp4Net.Services.ConnectionTests;

namespace Oftp4Net.Server.Components.Pages;

public partial class ConnectionTests : ComponentBase, IDisposable
{
    [Inject] protected ConnectionTestService Tests { get; set; } = null!;
    [Inject] protected IDataService DataService { get; set; } = null!;
    [Inject] protected IHxMessengerService Messenger { get; set; } = null!;
    [Inject] protected PartnerTlsTrustService TlsTrust { get; set; } = null!;
    [Inject] protected ITimeService TimeService { get; set; } = null!;
    [CascadingParameter] private Task<AuthenticationState>? AuthenticationState { get; set; }

    private List<Partner> partners = [];
    // null until loaded: the page renders while the partners are read, and a select without the item of its value fails
    private List<Identity>? identities;
    private int identityId;

    private bool CanStart => !Tests.IsRunning && identityId != 0;

    private List<Partner> Failed => partners
        .Where(p => Tests.Results.GetValueOrDefault(p.Id) is { Success: false })
        .ToList();

    protected override async Task OnInitializedAsync()
    {
        partners = (await DataService.GetAllPartnersAsync()).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        identities = await DataService.GetAllIdentitiesAsync();
        identityId = identities.FirstOrDefault()?.Id ?? 0;
        Tests.Changed += HandleChanged;
    }

    private void HandleChanged() => InvokeAsync(StateHasChanged);

    /// <summary>What the partner's TLS certificate is checked against now.</summary>
    private string TrustedText(Partner partner) =>
        (certificates.FirstOrDefault(c => c.Id == partner.TrustedCertificateId)?.Name ?? "-system trust store-") +
        (partner.AcceptInvalidTlsCertificate ? ", invalid certificates accepted" : "");

    private async Task<string?> GetUserAsync() =>
        AuthenticationState is null ? null : (await AuthenticationState).User.Identity?.Name;

    #region Sending a test file

    private HxModal testFileModal = null!;
    private Partner? testFilePartner;

    private async Task SendTestFileAsync(Partner partner)
    {
        testFilePartner = partner;
        await testFileModal.ShowAsync();
    }

    #endregion

    #region Changing a partner and testing again

    private HxModal partnerEditModal = null!;
    private Partner? editedPartner;
    private List<Certificate> certificates = [];

    private async Task EditPartnerAsync(Partner partner)
    {
        certificates = await DataService.GetAllCertificatesAsync();
        editedPartner = partner;
        await partnerEditModal.ShowAsync();
    }

    private async Task HandlePartnerSavedAsync(Partner partner)
    {
        await partnerEditModal.HideAsync();
        Start([partner]);
    }

    #endregion

    #region Settling a refused TLS certificate

    /// <summary>The certificate a partner presented in its last test, and why it was refused.</summary>
    private sealed record PresentedCertificate(
        byte[] Data, string Problem, string Subject, string Issuer, DateTime ValidFrom, DateTime ValidTo,
        bool IsValidNow, string Sha256Fingerprint, IReadOnlyList<string> Names, IReadOnlyList<string> OdetteIds);

    private HxModal certificateModal = null!;
    private Partner? certificatePartner;
    private PresentedCertificate? presented;
    private bool saving;

    /// <summary>Names of <see cref="OtherNames"/> that resolve here, so that calling them can work.</summary>
    private HashSet<string> resolvingNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Names the certificate is issued for that differ from the host the partner is called at; only offered when the
    /// name is all that is wrong, since calling another name does not make a certificate trusted.
    /// </summary>
    private List<string> OtherNames => presented is not { Problem: OftpCertificateProblems.NameMismatch } || certificatePartner is null
        ? []
        : presented.Names
            .Where(n => !n.Contains('*') && !string.Equals(n, certificatePartner.Host.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

    private async Task ShowCertificateAsync(Partner partner, ConnectionTestResult result)
    {
        if (result is not { RemoteCertificateData: { } data, CertificateProblem: { } problem })
            return;

        using var certificate = X509CertificateLoader.LoadCertificate(data);
        var now = TimeService.GetCurrentTime();
        presented = new PresentedCertificate(data, problem, certificate.Subject, certificate.Issuer,
            certificate.NotBefore, certificate.NotAfter, certificate.NotBefore <= now && certificate.NotAfter >= now,
            string.Join(":", Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).Chunk(2).Select(c => new string(c))),
            Names(certificate), OdetteCertificate.Ids(certificate).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        certificates = await DataService.GetAllCertificatesAsync();
        certificatePartner = partner;
        // A name of a certificate may be one of the partner's internal network only (seen with Porsche).
        var resolving = await Task.WhenAll(OtherNames.Select(async n => (Name: n, Resolves: await ResolvesAsync(n))));
        resolvingNames = resolving.Where(r => r.Resolves).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await certificateModal.ShowAsync();
    }

    private static async Task<bool> ResolvesAsync(string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            return (await System.Net.Dns.GetHostAddressesAsync(name, timeout.Token)).Length > 0;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>The DNS names of the certificate, or its common name when it has none.</summary>
    private static List<string> Names(X509Certificate2 certificate)
    {
        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(e => e.EnumerateDnsNames())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0 && certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false) is { Length: > 0 } common &&
            Uri.CheckHostName(common) == UriHostNameType.Dns)
            names.Add(common);
        return names;
    }

    private Task UseHostAsync(string host) => SettleAsync(async partner =>
    {
        partner.Host = host;
        await DataService.SavePartnerAsync(partner);
        return $"{partner.Name} is called at {host} now.";
    });

    private Task TrustAsync() => SettleAsync(async partner =>
    {
        var certificate = await TlsTrust.TrustPresentedCertificateAsync(partner, presented!.Data, await GetUserAsync());
        return $"{certificate.Name} is the trusted certificate of {partner.Name} now.";
    });

    private Task AcceptInvalidAsync() => SettleAsync(async partner =>
    {
        await TlsTrust.AcceptInvalidCertificateAsync(partner, await GetUserAsync());
        return $"{partner.Name} is called although its certificate is not valid.";
    });

    /// <summary>Applies a change to the partner, closes the dialog and tests the partner again.</summary>
    private async Task SettleAsync(Func<Partner, Task<string>> change)
    {
        if (certificatePartner is not { } partner || presented is null)
            return;

        saving = true;
        try
        {
            Messenger.AddInformation(await change(partner));
        }
        catch (Exception ex)
        {
            Messenger.AddError(ex.Message);
            return;
        }
        finally
        {
            saving = false;
        }

        await certificateModal.HideAsync();
        Start([partner]);
    }

    #endregion

    private void Start(IReadOnlyList<Partner> selected)
    {
        if (!Tests.Start(selected.Select(p => p.Id).ToList(), identityId))
            Messenger.AddWarning("Connection tests are running already.");
    }

    internal static ThemeColor StageColor(ConnectionTestResult result) => result.Stage switch
    {
        ConnectionTestStage.Completed => ThemeColor.Success,
        ConnectionTestStage.Skipped => ThemeColor.Secondary,
        _ => ThemeColor.Danger,
    };

    internal static string StageText(ConnectionTestResult result) => result.Stage switch
    {
        ConnectionTestStage.Completed => "OK",
        ConnectionTestStage.Skipped => "skipped",
        ConnectionTestStage.Setup => "failed: setup",
        ConnectionTestStage.Connect => "failed: connection",
        ConnectionTestStage.Tls => "failed: TLS",
        ConnectionTestStage.Start => "failed: SSID",
        ConnectionTestStage.SecureAuthentication => "failed: authentication",
        _ => result.Stage.ToString(),
    };

    public void Dispose() => Tests.Changed -= HandleChanged;
}
