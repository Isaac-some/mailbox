using System.Text.RegularExpressions;

namespace MailArchiver.Services;

public static partial class CsvImportValuePolicy
{
    public static string NormalizeEmail(string? value)
    {
        var email = (value ?? string.Empty).Trim();
        return LeadingRtfControlWords().Replace(email, string.Empty).Trim();
    }

    [GeneratedRegex(@"^(?:\\[a-zA-Z]+-?\d*\s*)+")]
    private static partial Regex LeadingRtfControlWords();
}
