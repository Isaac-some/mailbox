using System.ComponentModel.DataAnnotations;
using MailArchiver.Services;

namespace MailArchiver.ViewModels;

public sealed class MailDomainSettingsViewModel
{
    [Required, StringLength(253)] public string Domain { get; set; } = string.Empty;
    [Required, StringLength(253)] public string IncomingHost { get; set; } = string.Empty;
    [Range(1, 65535)] public int IncomingPort { get; set; } = 993;
    public bool IncomingImplicitTls { get; set; } = true;
    public string IncomingUsername { get; set; } = "%EMAILADDRESS%";
    [Required, StringLength(253)] public string OutgoingHost { get; set; } = string.Empty;
    [Range(1, 65535)] public int OutgoingPort { get; set; } = 587;
    public bool OutgoingImplicitTls { get; set; }
    public string OutgoingUsername { get; set; } = "%EMAILADDRESS%";
    [StringLength(2048)] public string? OAuthTokenEndpoint { get; set; }
    public bool OAuthRequiresSecret { get; set; }
    public bool OAuthRequiresRedirect { get; set; }
    public IReadOnlyList<MailDomainProfile> Profiles { get; set; } = [];
}
