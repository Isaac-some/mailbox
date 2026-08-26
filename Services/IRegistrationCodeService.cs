using MailArchiver.Models;
using MailArchiver.ViewModels;

namespace MailArchiver.Services;

public interface IRegistrationCodeService
{
    Task<string> CreateAsync(int createdByUserId, int validDays);
    Task<IReadOnlyList<RegistrationCodeViewModel>> GetAllAsync();
    Task<bool> RevokeAsync(int id);
    Task<RegistrationResult> RegisterAsync(
        string authorizationCode,
        string username,
        string email,
        string password);
}

public sealed record RegistrationResult(bool Succeeded, string? Error, User? User)
{
    public static RegistrationResult Success(User user) => new(true, null, user);
    public static RegistrationResult Failure(string error) => new(false, error, null);
}
