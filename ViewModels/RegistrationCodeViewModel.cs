namespace MailArchiver.ViewModels;

public sealed class RegistrationCodeViewModel
{
    public int Id { get; init; }
    public required string CodePrefix { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public DateTime? UsedAt { get; init; }
    public DateTime? RevokedAt { get; init; }
    public string? UsedByUsername { get; init; }
}
