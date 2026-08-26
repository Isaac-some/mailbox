namespace MailArchiver.Models;

public sealed class RegistrationCode
{
    public int Id { get; set; }
    public required string CodeHash { get; set; }
    public required string CodePrefix { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public int CreatedByUserId { get; set; }
    public int? UsedByUserId { get; set; }
}
