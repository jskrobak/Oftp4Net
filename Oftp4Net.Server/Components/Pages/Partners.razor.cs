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
using Oftp4Net.Services.Oftp;
using Oftp4Net.Services.Pdx;

namespace Oftp4Net.Server.Components.Pages;

public partial class Partners : ComponentBase
{
    [Inject] protected IDataService DataService { get; set; } = null!;
    [Inject] protected IHxMessengerService Messenger { get; set; } = null!;
    [Inject] protected IHxMessageBoxService MessageBox { get; set; } = null!;
    [Inject] protected ListenerService ListenerService { get; set; } = null!;
    [Inject] protected GlobalSettingsService GlobalSettingsService { get; set; } = null!;
    
    private Partner currentPartner = new();
    private PartnerRow? selectedRow;
    private HashSet<PartnerRow> selectedItems = [];
    private PartnerFilter filterModel = new();
    private HxGrid<PartnerRow> gridComponent = null!;
    private HxModal partnerEditModal = null!;
    
    private List<Certificate> availableCertificates = [];
    private List<Identity> availableIdentities = [];

    [Inject] protected Os4xPartnerImporter Os4xImporter { get; set; } = null!;

    private HxModal importModal = null!;
    private readonly Os4xImportOptions importOptions = new();
    private List<Os4xPartnerCandidate>? importCandidates;
    private List<Os4xCertificateCandidate> importCertificates = [];
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
        availableIdentities = await DataService.GetAllIdentitiesAsync();
        openSetups = await SetupDocuments.GetOpenAsync();
    }

    private bool PdxCanBeScheduled => pdxPlan is { CanApply: true, IsNew: false, Document: { } document } &&
                                      document.ValidFrom > DateTimeOffset.Now;

    private async Task<string?> GetUserAsync() =>
        AuthenticationState is null ? null : (await AuthenticationState).User.Identity?.Name;
    
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

    /// <summary>
    /// Partners with their sub-stations right under them. The sort keys are the partner's, and the sort is stable,
    /// so that a sub-station stays under its partner.
    /// </summary>
    private async Task<GridDataProviderResult<PartnerRow>> GetGridData(GridDataProviderRequest<PartnerRow> request)
    {
        var partners = await DataService.GetAllPartnersAsync();
        var rows = partners
            .Where(MatchesFilter)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(p => (p.SubStations ?? [])
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => new PartnerRow(p, s))
                .Prepend(new PartnerRow(p, null)));
        return request.ApplyTo(rows);
    }

    /// <summary>A partner is listed when its name or the name or SFID of one of its sub-stations matches.</summary>
    private bool MatchesFilter(Partner partner)
    {
        if (string.IsNullOrEmpty(filterModel.Name))
            return true;

        return Contains(partner.Name) ||
               (partner.SubStations ?? []).Any(s => Contains(s.Name) || Contains(s.SFID));

        bool Contains(string? text) => text?.Contains(filterModel.Name, StringComparison.OrdinalIgnoreCase) == true;
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
        if (selectedRow is null)
            return;

        if (selectedRow.SubStation is { } sub)
            await HandleEditSubStationClick(selectedRow.Partner, sub);
        else
            await HandleEditClick(selectedRow.Partner);
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
            var deleted = selectedItems.Where(r => r.SubStation is null).Select(r => r.Partner).ToHashSet();
            foreach (var partner in deleted)
                await DataService.DeletePartnerAsync(partner);

            // Sub-stations of partners that stay.
            foreach (var group in selectedItems.Where(r => r.SubStation is not null && !deleted.Contains(r.Partner)).GroupBy(r => r.Partner))
            {
                foreach (var row in group)
                    SubStationEditor.Remove(group.Key, row.SubStation!.SFID);
                await DataService.SavePartnerAsync(group.Key);
            }

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

    #region Sending one of our certificates (Odette OP08 2.5)

    private HxModal certificateModal = null!;
    private Partner? certificatePartner;
    private List<Identity>? certificateIdentities;
    private List<Certificate> ownCertificates = [];
    private int? ownCertificateId;

    private async Task HandleSendCertificateClick(Partner partner)
    {
        certificateIdentities = await DataService.GetAllIdentitiesAsync();
        ownCertificates = (await DataService.GetAllCertificatesAsync()).Where(c => c.HasPrivateKey).ToList();
        ownCertificateId = (await GlobalSettingsService.GetGlobalSettingsAsync()).FileSecurityCertificateId;
        certificatePartner = partner;
        await certificateModal.ShowAsync();
    }

    #endregion

    #region Sending a test file

    private HxModal testFileModal = null!;
    private Partner? testFilePartner;
    private PartnerSubStation? testFileStation;
    private List<Identity>? testFileIdentities;

    private async Task HandleSendTestFileClick(Partner partner, PartnerSubStation? station)
    {
        testFileIdentities = await DataService.GetAllIdentitiesAsync();
        testFilePartner = partner;
        testFileStation = station;
        await testFileModal.ShowAsync();
    }

    #endregion

    #region Testing the connection to one partner

    private HxModal connectionTestModal = null!;
    private Partner? connectionTestPartner;
    private List<Identity>? connectionTestIdentities;

    private async Task HandleTestConnectionClick(Partner partner)
    {
        connectionTestIdentities = await DataService.GetAllIdentitiesAsync();
        connectionTestPartner = partner;
        await connectionTestModal.ShowAsync();
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
            availableIdentities = await DataService.GetAllIdentitiesAsync();
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
            importCertificates = await Os4xImporter.LoadCertificatesAsync(importOptions);
            var importable = importCandidates.Count(c => c.CanImport);
            Messenger.AddInformation($"{importCandidates.Count} partner(s) found, {importable} can be imported; " +
                                     $"{importCertificates.Count} trusted certificate(s).");
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
        var selectedCertificates = importCertificates.Where(c => c is { Selected: true, CanImport: true }).ToList();
        if (selected.Count == 0 && selectedCertificates.Count == 0)
        {
            Messenger.AddWarning("No partner or certificate is selected.");
            return;
        }

        importRunning = true;
        try
        {
            // Certificates first: the partners' TLS connections can use them right away.
            var importedCertificates = await Os4xImporter.ImportCertificatesAsync(selectedCertificates);
            var result = await Os4xImporter.ImportAsync(selected);

            foreach (var error in result.Errors)
                Messenger.AddWarning(error);

            Messenger.AddInformation($"{result.Partners} partner(s), {result.SubStations} sub-station(s), " +
                                     $"{result.Identities} identity(ies), {result.Certificates} partner certificate(s) and " +
                                     $"{importedCertificates} trusted certificate(s) imported.");

            availableCertificates = await DataService.GetAllCertificatesAsync();
            await ListenerService.RefreshTrustedCertificatesAsync();

            availableIdentities = await DataService.GetAllIdentitiesAsync();
            await importModal.HideAsync();
            importCandidates = null;
            importCertificates = [];
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

    private async Task HandlePartnerSaved()
    {
        await gridComponent.RefreshDataAsync();
        await partnerEditModal.HideAsync();
    }

    private async Task HandleEditClick(Partner partner)
    {
        currentPartner = partner;
        await partnerEditModal.ShowAsync();
    }

    #region Sub-stations

    private HxModal subStationModal = null!;
    private Partner? subStationPartner;

    /// <summary>SFID of the sub-station being edited, <c>null</c> for a new one.</summary>
    private string? subStationOriginalSfid;

    /// <summary>A copy of the sub-station, so that closing the dialog without saving leaves the partner as it was.</summary>
    private PartnerSubStation? editedSubStation;

    private string? subStationError;

    private async Task HandleAddSubStationClick(Partner partner)
    {
        subStationPartner = partner;
        subStationOriginalSfid = null;
        editedSubStation = new PartnerSubStation();
        subStationError = null;
        await subStationModal.ShowAsync();
    }

    private async Task HandleEditSubStationClick(Partner partner, PartnerSubStation subStation)
    {
        subStationPartner = partner;
        subStationOriginalSfid = subStation.SFID;
        editedSubStation = SubStationEditor.Copy(subStation);
        subStationError = null;
        await subStationModal.ShowAsync();
    }

    private async Task SaveSubStation()
    {
        if (subStationPartner is null || editedSubStation is null)
            return;

        subStationError = SubStationEditor.Validate(subStationPartner, subStationOriginalSfid, editedSubStation);
        if (subStationError is not null)
            return;

        try
        {
            SubStationEditor.Save(subStationPartner, subStationOriginalSfid, editedSubStation);
            await DataService.SavePartnerAsync(subStationPartner);
        }
        catch (Exception ex)
        {
            subStationError = $"Saving failed: {ex.Message}";
            return;
        }

        await subStationModal.HideAsync();
        await gridComponent.RefreshDataAsync();
    }

    private async Task HandleDeleteSubStationClick(Partner partner, PartnerSubStation subStation)
    {
        SubStationEditor.Remove(partner, subStation.SFID);
        await DataService.SavePartnerAsync(partner);
        await gridComponent.RefreshDataAsync();
    }

    #endregion
}

/// <summary>A row of the partner list: a partner, or one of its sub-stations listed under it.</summary>
public sealed record PartnerRow(Partner Partner, PartnerSubStation? SubStation);