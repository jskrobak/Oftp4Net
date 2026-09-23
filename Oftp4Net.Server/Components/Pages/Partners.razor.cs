using System.Security.Authentication;
using Havit.Blazor.Components.Web;
using Havit.Blazor.Components.Web.Bootstrap;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.Domain;
using Oftp4Net.Services;
using Oftp4Net.Services.Import;
using Oftp4Net.Services.Pdx;

namespace Oftp4Net.Server.Components.Pages;

public partial class Partners : ComponentBase
{
    [Inject] protected IDataService DataService { get; set; } = null!;
    [Inject] protected IHxMessengerService Messenger { get; set; } = null!;
    [Inject] protected IHxMessageBoxService MessageBox { get; set; } = null!;
    [Inject] protected ListenerService ListenerService { get; set; } = null!;
    
    private Partner currentPartner = new();
    private HashSet<Partner> selectedItems = [];
    private PartnerFilter filterModel = new();
    private HxGrid<Partner> gridComponent = null!;
    private HxModal partnerEditModal = null!;
    
    private List<Certificate> availableCertificates = [];

    [Inject] protected Os4xPartnerImporter Os4xImporter { get; set; } = null!;

    private HxModal importModal = null!;
    private readonly Os4xImportOptions importOptions = new();
    private List<Os4xPartnerCandidate>? importCandidates;
    private bool importLoading;
    private bool importRunning;

    [Inject] protected PdxImporter PdxImporter { get; set; } = null!;
    [Inject] protected PartnerSetupService SetupService { get; set; } = null!;
    [Inject] protected IPartnerSetupDocumentRepository SetupDocuments { get; set; } = null!;
    [CascadingParameter] private Task<AuthenticationState>? AuthenticationState { get; set; }

    /// <summary>Datasheets waiting for approval or for their time.</summary>
    private List<PartnerSetupDocument> openSetups = [];

    /// <summary>Apply an uploaded datasheet at its valid from time instead of now.</summary>
    private bool pdxSchedule;

    private HxModal pdxModal = null!;
    private PdxImportPlan? pdxPlan;
    private bool pdxLoading;
    private bool pdxApplying;

    protected override async Task OnInitializedAsync()
    {
        availableCertificates = await DataService.GetAllCertificatesAsync();
        openSetups = await SetupDocuments.GetOpenAsync();
    }

    private bool PdxCanBeScheduled => pdxPlan is { CanApply: true, IsNew: false, Document: { } document } &&
                                      document.ValidFrom > DateTimeOffset.Now;

    private async Task<string?> GetUserAsync() =>
        AuthenticationState is null ? null : (await AuthenticationState).User.Identity?.Name;
    
    /// <summary>Code pages are only relevant when the content is converted in at least one direction.</summary>
    private bool ConversionConfigured =>
        currentPartner.OutgoingEncoding == FileCharacterEncoding.EBCDIC || currentPartner.ConvertIncomingEbcdicToAnsi;

    /// <summary>The cipher suite and the certificate are only relevant when something is secured.</summary>
    private bool FileSecurityConfigured =>
        currentPartner.SignFiles || currentPartner.EncryptFiles || currentPartner.RequestSignedEndResponse ||
        currentPartner.SecureAuthentication;

    private static string SecurityDescription(Partner partner)
    {
        var applied = new[]
        {
            partner.SignFiles ? "signed" : null,
            partner.CompressFiles ? "compressed" : null,
            partner.EncryptFiles ? "encrypted" : null,
            partner.RequestSignedEndResponse ? "signed EERP" : null,
            partner.SecureAuthentication ? "authenticated" : null,
            partner.RequireSignedFiles || partner.RequireEncryptedFiles || partner.RequireCompressedFiles
                ? "requires " + string.Join("/", new[]
                {
                    partner.RequireSignedFiles ? "signed" : null,
                    partner.RequireEncryptedFiles ? "encrypted" : null,
                    partner.RequireCompressedFiles ? "compressed" : null,
                }.Where(r => r is not null))
                : null,
        }.Where(a => a is not null).ToList();

        return applied.Count == 0 ? "-" : string.Join(", ", applied);
    }

    private static bool HasSetupDetails(Partner partner) =>
        partner.SetupAppliedDate is not null || !string.IsNullOrEmpty(partner.CompanyName) ||
        partner.Contacts is { Count: > 0 } || partner.SubStations is { Count: > 0 } ||
        partner.InboundDsnPatterns is { Count: > 0 } || partner.OutboundDsnPatterns is { Count: > 0 };

    private static string CompanyText(Partner partner) => string.Join(Environment.NewLine, new[]
    {
        partner.CompanyName,
        partner.Address,
        string.Join(" ", new[] { partner.ZipCode, partner.City }.Where(s => !string.IsNullOrEmpty(s))),
        partner.Country,
        string.IsNullOrEmpty(partner.Duns) ? null : $"DUNS {partner.Duns}",
    }.Where(s => !string.IsNullOrEmpty(s)));

    private static string SubStationText(PartnerSubStation sub)
    {
        var settings = new[]
        {
            Override("signed", sub.SignFiles),
            Override("encrypted", sub.EncryptFiles),
            Override("compressed", sub.CompressFiles),
            Override("signed EERP", sub.RequestSignedEndResponse),
            Override("requires signed", sub.RequireSignedFiles),
            Override("requires encrypted", sub.RequireEncryptedFiles),
            Override("requires compressed", sub.RequireCompressedFiles),
            sub.FileCipherSuite is null ? null : $"cipher suite {sub.FileCipherSuite}",
        }.Where(s => s is not null).ToList();

        return settings.Count == 0 ? "settings as the partner" : string.Join(", ", settings);

        static string? Override(string name, bool? value) => value switch
        {
            true => name,
            false => "not " + name,
            null => null,
        };
    }

    private static string EncodingDescription(Partner partner) =>
        partner.ConvertIncomingEbcdicToAnsi
            ? $"sent as {partner.OutgoingEncoding}, received EBCDIC → ANSI"
            : $"sent as {partner.OutgoingEncoding}";

    private List<SslProtocols> GetTlsVersions()
    {
        return Enum.GetValues<SslProtocols>().ToList();
    }
                            
    private List<int> SelectedTlsVersions
    {
        get => GetTlsVersions().Where(p => p != SslProtocols.None && currentPartner.Tls.HasFlag(p)).Select(p => (int)p).ToList();
        set => currentPartner.Tls = value.Aggregate((SslProtocols)0, (current, item) => current | (SslProtocols)item);
    }
    
    private async Task<GridDataProviderResult<Partner>> GetGridData(GridDataProviderRequest<Partner> request)
    {
        var response = await DataService.GetPartnersDataFragmentAsync(filterModel, request, request.CancellationToken);
        return new GridDataProviderResult<Partner>()
        {
            Data = response.Data,
            TotalCount = response.TotalCount
        };
    }
    
    private async Task HandleDeleteClick(Partner partner)
    {
        await DataService.DeletePartnerAsync(partner);
        await ListenerService.RefreshTrustedCertificatesAsync();
        await gridComponent.RefreshDataAsync();
    }
    
    private async Task HandleNewItemClicked()
    {
        currentPartner = new Partner();
        await partnerEditModal.ShowAsync();
    }

    private async Task HandleSelectedDataItemChanged()
    {
        // Clicking a selected row deselects it and sets the item to null.
        if (currentPartner is null)
            return;

        await partnerEditModal.ShowAsync();
    }


    private async Task HandleDeleteSelected()
    {
        if (selectedItems.Count == 0)
        {
            Messenger.AddWarning("No item is selected.");
            return;
        }

        if (!await MessageBox.ConfirmAsync("Delete", $"Delete {selectedItems.Count} selected item(s)?"))
            return;

        try
        {
            foreach (var item in selectedItems.ToList())
                await DataService.DeletePartnerAsync(item);
            await ListenerService.RefreshTrustedCertificatesAsync();
        }
        catch (Exception ex)
        {
            Messenger.AddError($"Delete failed: {ex.Message}");
        }

        selectedItems.Clear();
        await gridComponent.RefreshDataAsync();
    }

    #region Sending our OFTP2 Communication Setup (PDX)

    private HxModal datasheetModal = null!;
    private Partner? datasheetPartner;
    private List<Identity>? datasheetIdentities;

    private async Task HandleSendDatasheetClick(Partner partner)
    {
        datasheetIdentities = await DataService.GetAllIdentitiesAsync();
        datasheetPartner = partner;
        await datasheetModal.ShowAsync();
    }

    #endregion

    #region Import of an OFTP2 Communication Setup (PDX)

    private async Task HandlePdxImportClicked()
    {
        pdxPlan = null;
        await pdxModal.ShowAsync();
    }

    /// <summary>Reads the datasheet and shows what importing it would change.</summary>
    private async Task HandlePdxFileSelected(InputFileChangeEventArgs args)
    {
        pdxPlan = null;
        if (args.File.Size > PdxImporter.MaxSize)
        {
            Messenger.AddError($"The file is too large for a datasheet ({args.File.Size / 1024} kB).");
            return;
        }

        pdxLoading = true;
        try
        {
            using var content = new MemoryStream();
            await args.File.OpenReadStream(PdxImporter.MaxSize).CopyToAsync(content);
            pdxPlan = await PdxImporter.PlanAsync(content.ToArray());
            pdxSchedule = PdxCanBeScheduled;
        }
        catch (Exception ex)
        {
            Messenger.AddError($"Reading the datasheet failed: {ex.Message}");
        }
        finally
        {
            pdxLoading = false;
        }
    }

    private async Task ApplyPdx()
    {
        if (pdxPlan is not { CanApply: true })
            return;

        pdxApplying = true;
        try
        {
            if (pdxSchedule && PdxCanBeScheduled)
            {
                await SetupService.ScheduleUploadAsync(pdxPlan, await GetUserAsync());
                Messenger.AddInformation($"The datasheet of {pdxPlan.PartnerName} is applied at {pdxPlan.Document!.ValidFrom.LocalDateTime:g}.");
            }
            else
            {
                var partner = await PdxImporter.ApplyAsync(pdxPlan, await GetUserAsync());
                Messenger.AddInformation(pdxPlan.IsNew
                    ? $"Partner {partner.Name} created from the datasheet."
                    : $"Partner {partner.Name} updated from the datasheet.");
            }

            openSetups = await SetupDocuments.GetOpenAsync();

            await ListenerService.RefreshTrustedCertificatesAsync();
            availableCertificates = await DataService.GetAllCertificatesAsync();
            await pdxModal.HideAsync();
            pdxPlan = null;
            await gridComponent.RefreshDataAsync();
        }
        catch (Exception ex)
        {
            Messenger.AddError($"Import failed: {ex.Message}");
        }
        finally
        {
            pdxApplying = false;
        }
    }

    #endregion

    #region Import from OS4X

    private async Task HandleImportClicked()
    {
        importCandidates = null;
        await importModal.ShowAsync();
    }

    /// <summary>Reads the partner table of the OS4X installation and shows what the import would create.</summary>
    private async Task LoadOs4xPartners()
    {
        importLoading = true;
        try
        {
            importCandidates = await Os4xImporter.LoadAsync(importOptions);
            var importable = importCandidates.Count(c => c.CanImport);
            Messenger.AddInformation($"{importCandidates.Count} partner(s) found, {importable} can be imported.");
        }
        catch (Exception ex)
        {
            importCandidates = null;
            Messenger.AddError($"Reading the OS4X database failed: {ex.Message}");
        }
        finally
        {
            importLoading = false;
        }
    }

    private async Task ImportOs4xPartners()
    {
        if (importCandidates is null)
            return;

        var selected = importCandidates.Where(c => c is { Selected: true, CanImport: true }).ToList();
        if (selected.Count == 0)
        {
            Messenger.AddWarning("No partner is selected.");
            return;
        }

        importRunning = true;
        try
        {
            var result = await Os4xImporter.ImportAsync(selected);

            foreach (var error in result.Errors)
                Messenger.AddWarning(error);

            Messenger.AddInformation(result.Identities > 0
                ? $"{result.Partners} partner(s) and {result.Identities} identity(ies) imported."
                : $"{result.Partners} partner(s) imported.");

            await importModal.HideAsync();
            importCandidates = null;
            await gridComponent.RefreshDataAsync();
        }
        catch (Exception ex)
        {
            Messenger.AddError($"Import failed: {ex.Message}");
        }
        finally
        {
            importRunning = false;
        }
    }

    #endregion

    private async Task SavePartner()
    {
        await DataService.SavePartnerAsync(currentPartner);
        await ListenerService.RefreshTrustedCertificatesAsync();
        
        await gridComponent.RefreshDataAsync();
        await partnerEditModal.HideAsync();
    }   

    private async Task HandleEditClick(Partner partner)
    {
        currentPartner = partner;
        await partnerEditModal.ShowAsync();
    }
}