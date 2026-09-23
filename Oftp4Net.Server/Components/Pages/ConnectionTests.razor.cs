using Havit.Blazor.Components.Web;
using Havit.Blazor.Components.Web.Bootstrap;
using Microsoft.AspNetCore.Components;
using Oftp4Net.Domain;
using Oftp4Net.Services;
using Oftp4Net.Services.ConnectionTests;

namespace Oftp4Net.Server.Components.Pages;

public partial class ConnectionTests : ComponentBase, IDisposable
{
    [Inject] protected ConnectionTestService Tests { get; set; } = null!;
    [Inject] protected IDataService DataService { get; set; } = null!;
    [Inject] protected IHxMessengerService Messenger { get; set; } = null!;

    private List<Partner> partners = [];
    private List<Identity> identities = [];
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
