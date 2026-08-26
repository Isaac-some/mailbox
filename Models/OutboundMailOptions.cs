namespace MailArchiver.Models;

public sealed class OutboundMailOptions
{
    public const string SectionName = "OutboundMail";

    public bool Enabled { get; set; }
    public int MaxAttachmentCount { get; set; } = 10;
    public long MaxTotalAttachmentBytes { get; set; } = 10 * 1024 * 1024;
    public string SmtpHost { get; set; } = "smtp-mail.outlook.com";
    public int SmtpPort { get; set; } = 587;
}
