using System.Text.RegularExpressions;

namespace MailArchiver.Services;

public static partial class WhitelistDeletionParser
{
    [GeneratedRegex(
        @"(?<![A-Z0-9.!#$%&'*+/=?^_`{|}~-])(?<email>[A-Z0-9.!#$%&'*+/=?^_`{|}~-]+@(?:[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?\.)+com)(?![A-Z0-9.-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ComEmailPattern();

    public static IReadOnlySet<string> ExtractComAddresses(string? sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return ComEmailPattern()
            .Matches(sourceText)
            .Cast<Match>()
            .Select(match => match.Groups["email"].Value.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
