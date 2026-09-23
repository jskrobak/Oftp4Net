using Havit.Blazor.Components.Web;
using Havit.Blazor.Components.Web.Bootstrap;
using Microsoft.AspNetCore.Components;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services;
using Oftp4Net.Services.Tsl;

namespace Oftp4Net.Server.Components.Pages;

public partial class Settings : ComponentBase
{
    [Inject] protected GlobalSettingsService GlobalSettingsService { get; set; } = null!;
    [Inject] protected IDataService DataService { get; set; } = null!;
    [Inject] protected SendService SendService { get; set; } = null!;
    [Inject] protected IHxMessengerService Messenger { get; set; } = null!;
    [Inject] protected TslService Tsl { get; set; } = null!;

    private bool tslRefreshing;

    /// <summary>The administrator cleared the trusted signer; otherwise the stored one is kept on saving.</summary>
    private bool tslSignerCleared;

    private void ClearTslSigner()
    {
        settings!.TslSignerThumbprint = null;
        tslSignerCleared = true;
    }

    private Services.GlobalSettings? settings;
    private List<Certificate> availableCertificates = [];
    private List<Listener> availableListeners = [];

    [Inject] protected IListenerRepository Listeners { get; set; } = null!;

    private string? AddressText
    {
        get => string.Join(Environment.NewLine, settings!.StationProfile.AddressLines);
        set => settings!.StationProfile.AddressLines = (value ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private string? OutboundIpsText
    {
        get => string.Join(", ", settings!.StationProfile.OutboundIps);
        set => settings!.StationProfile.OutboundIps = Split(value);
    }

    private static List<string> Split(object? value) =>
        (value?.ToString() ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    protected override async Task OnInitializedAsync()
    {
        // Certificates first: the selects of the form need them as soon as the settings are rendered.
        availableCertificates = (await DataService.GetAllCertificatesAsync()).Where(c => c.HasPrivateKey).ToList();
        availableListeners = await Listeners.GetAllAsync();
        settings = await GlobalSettingsService.GetGlobalSettingsAsync();
    }

    /// <summary>A security feature of the station profile with its setting for received and sent files.</summary>
    private sealed record ProfileRow(
        string Name,
        Func<SecurityUsage> Inbound,
        Action<SecurityUsage> SetInbound,
        Func<SecurityUsage> Outbound,
        Action<SecurityUsage> SetOutbound);

    private IEnumerable<ProfileRow> ProfileRows
    {
        get
        {
            var profile = settings!.StationProfile;
            yield return new ProfileRow("File signature",
                () => profile.InboundFileSignature, v => profile.InboundFileSignature = v,
                () => profile.OutboundFileSignature, v => profile.OutboundFileSignature = v);
            yield return new ProfileRow("File encryption",
                () => profile.InboundFileEncryption, v => profile.InboundFileEncryption = v,
                () => profile.OutboundFileEncryption, v => profile.OutboundFileEncryption = v);
            yield return new ProfileRow("File compression",
                () => profile.InboundFileCompression, v => profile.InboundFileCompression = v,
                () => profile.OutboundFileCompression, v => profile.OutboundFileCompression = v);
            yield return new ProfileRow("Signed EERP",
                () => profile.InboundEerpSignature, v => profile.InboundEerpSignature = v,
                () => profile.OutboundEerpSignature, v => profile.OutboundEerpSignature = v);
        }
    }

    /// <summary>Downloads the trust list with the saved settings.</summary>
    private async Task RefreshTslAsync()
    {
        tslRefreshing = true;
        try
        {
            var status = await Tsl.RefreshAsync();
            if (status.Error is null && status.List is { } list)
                Messenger.AddInformation($"Trust list {list.SchemeName} {list.SequenceNumber} loaded.");
            // A pinned signer may have been taken over.
            var saved = await GlobalSettingsService.GetGlobalSettingsAsync();
            settings!.TslSignerThumbprint = saved.TslSignerThumbprint;
        }
        finally
        {
            tslRefreshing = false;
        }
    }

    private async Task SaveAsync()
    {
        // The signer may have been pinned by a download after this page was loaded.
        if (!tslSignerCleared)
            settings!.TslSignerThumbprint = (await GlobalSettingsService.GetGlobalSettingsAsync()).TslSignerThumbprint;
        tslSignerCleared = false;

        await GlobalSettingsService.SetGlobalSettingsAsync(settings!);
        // Apply a changed interval immediately.
        SendService.Trigger();
        Messenger.AddInformation("Settings saved.");
    }
}
