using Havit.Blazor.Components.Web;
using Havit.Blazor.Components.Web.Bootstrap;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Oftp4Net.Domain;
using Oftp4Net.Services;

namespace Oftp4Net.Server.Components.Pages;

public partial class Users : ComponentBase
{
    [Inject] protected UserService UserService { get; set; } = null!;
    [Inject] protected IHxMessengerService Messenger { get; set; } = null!;
    [Inject] protected IHxMessageBoxService MessageBox { get; set; } = null!;
    [CascadingParameter] private Task<AuthenticationState> AuthenticationState { get; set; } = null!;

    private List<User> users = [];
    private string currentUserName = "";
    private HxModal editModal = null!;
    private User? resetUser;
    private string newUserName = "";
    private string newPassword = "";
    private bool mustChangePassword = true;

    protected override async Task OnInitializedAsync()
    {
        currentUserName = (await AuthenticationState).User.Identity?.Name ?? "";
        await LoadAsync();
    }

    private async Task LoadAsync() => users = await UserService.GetAllAsync();

    private async Task ShowResetAsync(User user)
    {
        resetUser = user;
        await editModal.ShowAsync();
    }

    private async Task SaveAsync()
    {
        try
        {
            if (resetUser is null)
                await UserService.CreateAsync(newUserName, newPassword, mustChangePassword);
            else
                await UserService.ResetPasswordAsync(resetUser.Id, newPassword, mustChangePassword);
        }
        catch (InvalidOperationException ex)
        {
            Messenger.AddError(ex.Message);
            return;
        }

        newUserName = "";
        newPassword = "";
        mustChangePassword = true;
        await editModal.HideAsync();
        await LoadAsync();
    }

    private async Task DeleteAsync(User user)
    {
        if (!await MessageBox.ConfirmAsync("Delete user", $"Delete user {user.UserName}?"))
            return;

        try
        {
            await UserService.DeleteAsync(user.Id, currentUserName);
        }
        catch (InvalidOperationException ex)
        {
            Messenger.AddError(ex.Message);
        }

        await LoadAsync();
    }
}
