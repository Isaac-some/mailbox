namespace MailArchiver.ViewModels;

public sealed class WhitelistDeletionRequest
{
    public string SourceText { get; set; } = string.Empty;
    public string PreviewFingerprint { get; set; } = string.Empty;
}
