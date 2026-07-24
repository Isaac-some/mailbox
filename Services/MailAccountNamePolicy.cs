namespace MailArchiver.Services;

public static class MailAccountNamePolicy
{
    public static string Derive(string emailAddress)
    {
        var trimmed = emailAddress?.Trim() ?? string.Empty;
        var atIndex = trimmed.IndexOf('@');
        var localPart = atIndex >= 0 ? trimmed[..atIndex] : trimmed;
        var name = new string(localPart.Where(character => !char.IsDigit(character)).ToArray());

        return string.IsNullOrWhiteSpace(name) ? localPart : name;
    }
}
