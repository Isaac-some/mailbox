using System.ComponentModel.DataAnnotations;

namespace MailArchiver.Models;

public sealed class MailConcurrencyOptions
{
    public const string SectionName = "MailConcurrency";

    [Range(1, int.MaxValue)]
    public int AutomaticLimit { get; set; } = 2;

    [Range(2, 16)]
    public int PerDomainLimit { get; set; } = 4;
}
