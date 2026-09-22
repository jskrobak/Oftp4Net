using Havit.Blazor.Components.Web;
using Havit.Blazor.Components.Web.Bootstrap;
using Microsoft.AspNetCore.Components;
using Oftp4Net.Domain;
using Oftp4Net.Services;

namespace Oftp4Net.Server.Components.Pages;

public partial class Settings : ComponentBase
{
    [Inject] protected GlobalSettingsService GlobalSettingsService { get; set; } = null!;
    [Inject] protected IDataService DataService { get; set; } = null!;
    [Inject] protected SendService SendService { get; set; } = null!;
    [Inject] protected IHxMessengerService Messenger { get; set; } = null!;

    private Services.GlobalSettings? settings;
    private List<Certificate> availableCertificates = [];

    protected override async Task OnInitializedAsync()
    {
        settings = await GlobalSettingsService.GetGlobalSettingsAsync();
        availableCertificates = (await DataService.GetAllCertificatesAsync()).Where(c => c.HasPrivateKey).ToList();
    }

    private async Task SaveAsync()
    {
        await GlobalSettingsService.SetGlobalSettingsAsync(settings!);
        // Apply a changed interval immediately.
        SendService.Trigger();
        Messenger.AddInformation("Settings saved.");
    }
}
