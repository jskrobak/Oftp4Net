using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Oftp4Net.Server.Components.Layout;

public partial class MainLayout
{
    private const string TitleBase = "Oftp4Net";
    private const string TitleSeparator = " | ";

    [Inject] protected NavigationManager NavigationManager { get; set; } = null!;
    [CascadingParameter] private Task<AuthenticationState>? AuthenticationState { get; set; }

    private string _title = "Oftp4Net";

    protected override async Task OnParametersSetAsync()
    {
        // A user with an initial password (e.g. the default admin account) has to change it first.
        if (AuthenticationState is not null &&
            (await AuthenticationState).User.HasClaim(c => c.Type == AuthClaims.MustChangePassword))
        {
            NavigationManager.NavigateTo("ChangePassword", forceLoad: true);
            return;
        }

        var path = new Uri(NavigationManager.Uri).AbsolutePath.TrimEnd('/');

        var lastSegmentStart = path.LastIndexOf("/");
        if (lastSegmentStart > 0)
        {
            _title = path[(lastSegmentStart + 1)..] + TitleSeparator + TitleBase;
            return;
        }

        _title = TitleBase;
    }
}
