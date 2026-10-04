using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Oftp4Net.Services;

namespace Oftp4Net.Server;

/// <summary>
/// Checks the user of an open Blazor circuit again every minute. The circuit lives on after the cookie is
/// refused, so without this a deleted user or one whose password changed would keep the page they have open.
/// </summary>
public sealed class UserRevalidatingAuthenticationStateProvider(ILoggerFactory loggerFactory, IServiceScopeFactory scopeFactory)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState,
        CancellationToken cancellationToken)
    {
        // A circuit lives longer than a scope of the data layer would stay fresh.
        await using var scope = scopeFactory.CreateAsyncScope();
        return await AuthSessions.IsValidAsync(authenticationState.User,
            scope.ServiceProvider.GetRequiredService<UserService>(), cancellationToken);
    }
}
