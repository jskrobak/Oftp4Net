using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Havit.Blazor.Components.Web;
using Havit.Blazor.Components.Web.Bootstrap;
using Microsoft.AspNetCore.Components;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.Domain;
using Oftp4Net.Services;
using Oftp4Net.Services.Oftp;

namespace Oftp4Net.Server.Components.Pages;

public partial class Certificates : ComponentBase
{
    [Inject] protected IDataService DataService { get; set; } = null!;
    [Inject] protected IHxMessengerService Messenger { get; set; } = null!;
    [Inject] protected IHxMessageBoxService MessageBox { get; set; } = null!;
    [Inject] protected IUploadService UploadService { get; set; } = null!;
    [Inject] protected IFileService FileService { get; set; } = null!;
    
    
    private Certificate currentCertificate = new();
    private HashSet<Certificate> selectedItems = [];
    private CertificateFilter filterModel = new();
    private HxGrid<Certificate> gridComponent = null!;
    private HxModal certificateEditModal = null!;
    private HxOffcanvas importOffCanvasComponent = null!;
    private HxInputFile inputFileComponent = null!;
    private FileUploadedEventArgs? fileUploaded;
    private string inputPassword = "";
    
    
    private async Task<GridDataProviderResult<Certificate>> GetGridData(GridDataProviderRequest<Certificate> request)
    {
        var response = await DataService.GetCertificatesDataFragmentAsync(filterModel, request, request.CancellationToken);
        return new GridDataProviderResult<Certificate>()
        {
            Data = response.Data,
            TotalCount = response.TotalCount
        };
    }
    
    private async Task HandleDeleteClick(Certificate certificate)
    {
        await DataService.DeleteCertificateAsync(certificate);
        await gridComponent.RefreshDataAsync();
    }
    
    private async Task HandleNewItemClicked()
    {

        await importOffCanvasComponent.ShowAsync();
        
        //currentCertificate = new Certificate();
        //await certificateEditModal.ShowAsync();
    }

    private async Task HandleSelectedDataItemChanged()
    {
        // Clicking a selected row deselects it and sets the item to null.
        if (currentCertificate is null)
            return;

        await certificateEditModal.ShowAsync();
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
                await DataService.DeleteCertificateAsync(item);
        }
        catch (Exception ex)
        {
            Messenger.AddError($"Delete failed: {ex.Message}");
        }

        selectedItems.Clear();
        await gridComponent.RefreshDataAsync();
    }

    private async Task SaveCertificate()
    {
        await DataService.SaveCertificateAsync(currentCertificate);
        
        await gridComponent.RefreshDataAsync();
        await certificateEditModal.HideAsync();
    }   

    private async Task HandleEditClick(Certificate certificate)
    {
        currentCertificate = certificate;
        await certificateEditModal.ShowAsync();
    }

    private async Task HandleImport()
    {
        string? accessToken = null;
		
        // OPTIONAL: Authorization Bearer Token (JWT) to be used with the upload HTTP request
        //var accessTokenResult = await AccessTokenProvider.RequestAccessToken();
        //if (accessTokenResult.Status == AccessTokenResultStatus.Success)
        //{
        //	if (accessTokenResult.TryGetToken(out var token))
        //	{
        //		accessToken = token.Value;
        //	}
        //}

        await inputFileComponent.StartUploadAsync(accessToken);
    }

    private async Task HandleFileUploaded(FileUploadedEventArgs fileUploaded)
    {
        this.fileUploaded = fileUploaded;

        try
        {
            if (fileUploaded is not { ResponseStatus: HttpStatusCode.OK })
                throw new Exception($"Upload failed. Http status code: {fileUploaded.ResponseStatus}");

            var data = await UploadService.ReadAllBytesAsync(fileUploaded.ResponseText.Replace("\"", ""));

            var dbCert = CertificateLoader.CreateEntity(data, fileUploaded.OriginalFileName, inputPassword);

            await DataService.SaveCertificateAsync(dbCert);
            await gridComponent.RefreshDataAsync();
            
            Messenger.AddInformation("Import succeeded.");
        }
        catch (Exception ex)
        {
            Messenger.AddError($"Import failed. Error: {ex.Message}");
        }

        await importOffCanvasComponent.HideAsync();
    }
}
