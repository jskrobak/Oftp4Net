using Havit.Blazor.Components.Web;
using Havit.Blazor.Components.Web.Bootstrap;
using Microsoft.AspNetCore.Components;
using Oftp4Net.Domain;
using Oftp4Net.Services.Api;

namespace Oftp4Net.Server.Components.Pages;

public partial class ApiTokens : ComponentBase
{
    [Inject] protected ApiTokenService TokenService { get; set; } = null!;
    [Inject] protected WebhookDispatcher Webhooks { get; set; } = null!;
    [Inject] protected IHxMessengerService Messenger { get; set; } = null!;
    [Inject] protected IHxMessageBoxService MessageBox { get; set; } = null!;

    private List<ApiToken> tokens = [];
    private HxModal editModal = null!;
    private string? createdToken;
    private string newName = "";
    private DateTime? newExpiresAt;
    private string newWebhookUrl = "";
    private string newWebhookSecret = "";
    private bool newOutbox;
    private bool newInbox;
    private bool newCertificates;

    private ApiTokenScopes NewScopes =>
        (newOutbox ? ApiTokenScopes.Outbox : 0) | (newInbox ? ApiTokenScopes.Inbox : 0) |
        (newCertificates ? ApiTokenScopes.Certificates : 0);

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync() => tokens = await TokenService.GetAllAsync();

    private async Task ShowNewAsync()
    {
        createdToken = null;
        newName = "";
        newExpiresAt = null;
        newWebhookUrl = "";
        newWebhookSecret = "";
        newOutbox = newInbox = newCertificates = false;
        await editModal.ShowAsync();
    }

    private async Task CreateAsync()
    {
        if (!string.IsNullOrWhiteSpace(newWebhookUrl) && !Webhooks.IsAllowed(newWebhookUrl, out var error))
        {
            Messenger.AddError(error);
            return;
        }

        try
        {
            var (_, value) = await TokenService.CreateAsync(newName, NewScopes, newExpiresAt, newWebhookUrl, newWebhookSecret);
            createdToken = value;
        }
        catch (InvalidOperationException ex)
        {
            Messenger.AddError(ex.Message);
            return;
        }

        await editModal.HideAsync();
        await LoadAsync();
    }

    private async Task ToggleAsync(ApiToken token)
    {
        token.Enabled = !token.Enabled;
        await TokenService.SaveAsync(token);
        await LoadAsync();
    }

    private async Task DeleteAsync(ApiToken token)
    {
        if (!await MessageBox.ConfirmAsync("Delete token", $"Delete token {token.Name}? Systems using it will stop working."))
            return;

        await TokenService.DeleteAsync(token.Id);
        await LoadAsync();
    }
}
