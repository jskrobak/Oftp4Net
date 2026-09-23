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
    private string newEmail = "";
    private bool mustChangePassword = true;
    private HxModal emailModal = null!;
    private User? emailUser;

    protected override async Task OnInitializedAsync()
    {
        currentUserName = (await AuthenticationState).User.Identity?.Name ?? "";
        await LoadAsync();
    }

    private async Task LoadAsync() => users = await UserService.GetAllAsync();

    private async Task ShowResetAsync(User user)
    {
        resetUser = user;
        newPassword = "";
        await editModal.ShowAsync();
    }

    private async Task ShowEmailAsync(User user)
    {
        emailUser = user;
        newEmail = user.Email ?? "";
        await emailModal.ShowAsync();
    }

    private async Task SaveEmailAsync()
    {
        try
        {
            await UserService.SetEmailAsync(emailUser!.Id, newEmail);
        }
        catch (InvalidOperationException ex)
        {
            Messenger.AddError(ex.Message);
            return;
        }

        newEmail = "";
        await emailModal.HideAsync();
        await LoadAsync();
    }

    private async Task SaveAsync()
    {
        try
        {
            if (resetUser is null)
                await UserService.CreateAsync(newUserName, newPassword, mustChangePassword, newEmail);
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
        newEmail = "";
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
