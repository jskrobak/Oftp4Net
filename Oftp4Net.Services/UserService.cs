using Havit.Data.Patterns.UnitOfWorks;
using Havit.Services.TimeServices;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.Services;

/// <summary>Users of the administration UI: sign in, password changes and the default account.</summary>
public class UserService(
    IUserRepository userRepository,
    IUnitOfWork unitOfWork,
    ITimeService timeService,
    ILogger<UserService> logger)
{
    public const string DefaultUserName = "admin";
    public const string DefaultPassword = "admin";
    public const int MinPasswordLength = 8;

    private static readonly PasswordHasher<User> PasswordHasher = new();

    /// <summary>
    /// Creates the account admin/admin when there is no user yet (empty database). The password must be changed
    /// after the first sign in.
    /// </summary>
    public async Task<bool> EnsureDefaultUserAsync(CancellationToken cancellationToken = default)
    {
        if (await userRepository.AnyAsync(cancellationToken))
            return false;

        var user = new User
        {
            UserName = DefaultUserName,
            MustChangePassword = true,
            Created = timeService.GetCurrentTime(),
        };
        user.PasswordHash = PasswordHasher.HashPassword(user, DefaultPassword);

        unitOfWork.AddForInsert(user);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogWarning("Created the default user '{UserName}' with password '{Password}'. Change the password after signing in.",
            DefaultUserName, DefaultPassword);
        return true;
    }

    /// <summary>Returns the user when the credentials are valid, otherwise <c>null</c>.</summary>
    public async Task<User?> ValidateCredentialsAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByUserNameAsync(userName, cancellationToken);
        if (user is null)
        {
            // Hash anyway so that unknown user names cannot be detected by timing.
            PasswordHasher.HashPassword(new User(), password);
            return null;
        }

        var result = PasswordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
            return null;

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = PasswordHasher.HashPassword(user, password);

        user.LastLogin = timeService.GetCurrentTime();
        unitOfWork.AddForUpdate(user);
        await unitOfWork.CommitAsync(cancellationToken);
        return user;
    }

    public async Task<List<User>> GetAllAsync() => (await userRepository.GetAllAsync()).OrderBy(u => u.UserName).ToList();

    public async Task<User?> FindByUserNameAsync(string userName) => await userRepository.FindByUserNameAsync(userName);

    public async Task CreateAsync(string userName, string password, bool mustChangePassword)
    {
        var normalized = NormalizeUserName(userName);
        if (normalized.Length == 0)
            throw new InvalidOperationException("User name is required.");
        if (await userRepository.FindByUserNameAsync(normalized) is not null)
            throw new InvalidOperationException($"User '{normalized}' already exists.");
        ValidateNewPassword(password);

        var user = new User
        {
            UserName = normalized,
            MustChangePassword = mustChangePassword,
            Created = timeService.GetCurrentTime(),
        };
        user.PasswordHash = PasswordHasher.HashPassword(user, password);

        unitOfWork.AddForInsert(user);
        await unitOfWork.CommitAsync();
    }

    /// <summary>Sets a new password of the signed in user after verifying the current one.</summary>
    public async Task ChangePasswordAsync(string userName, string currentPassword, string newPassword)
    {
        var user = await userRepository.FindByUserNameAsync(userName)
                   ?? throw new InvalidOperationException("User not found.");

        if (PasswordHasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
            throw new InvalidOperationException("The current password is not correct.");
        if (currentPassword == newPassword)
            throw new InvalidOperationException("The new password must differ from the current one.");

        await SetPasswordAsync(user, newPassword, mustChangePassword: false);
    }

    /// <summary>Administrator reset of another user's password.</summary>
    public async Task ResetPasswordAsync(int userId, string newPassword, bool mustChangePassword)
    {
        var user = await userRepository.GetObjectAsync(userId);
        await SetPasswordAsync(user, newPassword, mustChangePassword);
    }

    public async Task DeleteAsync(int userId, string currentUserName)
    {
        var user = await userRepository.GetObjectAsync(userId);
        if (user.UserName == NormalizeUserName(currentUserName))
            throw new InvalidOperationException("You cannot delete your own account.");

        unitOfWork.AddForDelete(user);
        await unitOfWork.CommitAsync();
    }

    private async Task SetPasswordAsync(User user, string newPassword, bool mustChangePassword)
    {
        ValidateNewPassword(newPassword);
        user.PasswordHash = PasswordHasher.HashPassword(user, newPassword);
        user.MustChangePassword = mustChangePassword;
        unitOfWork.AddForUpdate(user);
        await unitOfWork.CommitAsync();
    }

    private static void ValidateNewPassword(string password)
    {
        if (password.Length < MinPasswordLength)
            throw new InvalidOperationException($"The password must have at least {MinPasswordLength} characters.");
    }

    private static string NormalizeUserName(string userName) => userName.Trim().ToLowerInvariant();
}
